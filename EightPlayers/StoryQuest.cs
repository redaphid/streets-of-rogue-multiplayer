using System;
using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers
{
    // GM/character STORY QUESTS (rogue-gm #22, and the completion signal of #7).
    // A concrete goal a character gives the player becomes something the game
    // shows like its OWN per-map quest, then reports back when it's done:
    //
    //   marker      an ALL-CAPS objective label pinned over the target, via the
    //               exact QuestMarker path Label.cs already rides (Labels.Apply).
    //   mission row a real Quest registered in gc.quests.mainQuestList and given
    //               a mission-sheet slot (AddQuest). It carries a sentinel
    //               questType ("EPStory") so the vanilla completion state
    //               machine (Quests.QuestUpdate) and the vanilla per-type
    //               renderer (QuestSlot.UpdateQuest switch) BOTH fall through —
    //               a Harmony postfix below draws our raw objective text into
    //               the row, and OUR polled Tick drives completion.
    //   completion  Tick watches the target (dead / a player reached it) per
    //               type and, when satisfied, tears the quest down and pushes a
    //               {"event":"quest_complete", ...} frame on the HttpChannel
    //               event stream — the finished-a-goal signal the GM lacked (#7).
    //
    // WHY POLLED, NOT NATIVE COMPLETION: only a few vanilla quest types have a
    // single-target completion check in QuestUpdate, and each is wired into
    // level-generation state (chunks, ownerIDs). reach/interact have no clean
    // native single-target semantics at all. Polling the target ourselves gives
    // the same four behaviors reliably without hijacking that machinery.
    //
    // Path 1 (fresh Quest) vs Path 2 (retarget an existing per-map quest): this
    // is Path 1. Path 2 means snapshotting and restoring a live quest's deeply
    // linked fields (questGiver/questEnder/chosenChunk/ownerIDs/secretHate) and
    // fighting its native QuestUpdate on every retarget — far more fragile.
    //
    // Everything native is best-effort (try/catch): if registration ever throws,
    // the marker + polled completion + event still work, and the game is never
    // put at risk. Uids churn per level, so all story quests are dropped on a
    // level change (same rule as labels) — re-add after level changes.
    internal static class StoryQuests
    {
        private sealed class Entry
        {
            public string Id;
            public StoryQuestType Type;
            public int TargetUid;          // 0 when the target is a bare position
            public Vector2 Pos;            // fallback/position anchor
            public bool HasEntity;
            public string Text;
            public Quest Native;           // best-effort native mission-sheet quest (may be null)
            public bool HasMarker;
        }

        private static readonly Dictionary<string, Entry> _quests = new Dictionary<string, Entry>();
        private static float _next;
        private static int _lastLevelKey = int.MinValue;

        // ---- add ------------------------------------------------------------

        /// <summary>Register a story quest. <paramref name="targetUid"/> is a live
        /// agent OR ObjectReal uid (0 when only a position is given). Pins the
        /// marker, best-effort registers a native mission-sheet row, and starts
        /// watching for completion.</summary>
        internal static string Add(string idRaw, int targetUid, Vector2? pos, string typeRaw, string textRaw)
        {
            var gc = GameController.gameController;
            if (gc == null || gc.quests == null)
                throw new InvalidOperationException("no game running");
            var id = StoryQuestCore.NormalizeId(idRaw);
            var type = StoryQuestCore.ParseType(typeRaw);
            var text = StoryQuestCore.NormalizeText(textRaw);

            PlayfieldObject target = null;
            Vector2 anchor;
            if (targetUid != 0)
            {
                target = GameStateApi.FindAgent(targetUid);
                if (target == null)
                    target = GameStateApi.FindObjectReal(targetUid);
                if (target == null)
                    throw new ArgumentException($"no agent or object with uid {targetUid}");
                anchor = target.tr != null ? (Vector2)target.tr.position : Vector2.zero;
            }
            else if (pos.HasValue)
            {
                anchor = pos.Value;
            }
            else
            {
                throw new ArgumentException("quest add needs a target: a uid, or x,y");
            }

            if (type == StoryQuestType.Kill && !(target is Agent))
                throw new ArgumentException("a kill quest needs an AGENT target (uid of a living agent)");

            Clear(id); // replace-in-place
            SyncLevelKey();

            var entry = new Entry
            {
                Id = id,
                Type = type,
                TargetUid = targetUid,
                Pos = anchor,
                HasEntity = target != null,
                Text = text,
            };

            // Marker over the target (entity targets only) — the reliable,
            // always-correct visible presence. Reuses the proven label path.
            if (target != null)
            {
                try { Labels.Apply(targetUid, text); entry.HasMarker = true; }
                catch (Exception e) { EightPlayersPlugin.Log.LogWarning($"story quest marker failed: {e.Message}"); }
            }

            // Best-effort native mission-sheet row.
            try { entry.Native = RegisterNative(gc, target, text); }
            catch (Exception e) { EightPlayersPlugin.Log.LogWarning($"story quest native register failed (marker still works): {e.Message}"); }

            _quests[id] = entry;
            var where = target != null
                ? $"over uid {targetUid}"
                : $"at ({anchor.x:0.#},{anchor.y:0.#})";
            var row = entry.Native != null ? "mission-sheet row + " : "";
            return $"story quest '{id}' [{StoryQuestCore.TypeName(type)}] added: {row}marker \"{text}\" {where}. Completes on {CompletionHint(type)}; uids churn per level — re-add after level changes.";
        }

        private static string CompletionHint(StoryQuestType t)
        {
            switch (t)
            {
                case StoryQuestType.Kill: return "target death";
                case StoryQuestType.Reach: return "a player reaching it";
                case StoryQuestType.Interact: return "a player getting to interaction range";
                case StoryQuestType.Protect: return "`quest done` (fails + reports if the target dies)";
                default: return "completion";
            }
        }

        /// <summary>Build + register a native Quest for the mission sheet. Custom
        /// questType keeps vanilla completion/rendering out of our way; the
        /// QuestSlot postfix draws the text. Throws are caught by the caller.</summary>
        private static Quest RegisterNative(GameController gc, PlayfieldObject target, string text)
        {
            if (gc.playerAgent == null || gc.quests.questSheet == null)
                return null;
            var q = new Quest
            {
                player = gc.playerAgent,
                questType = StoryQuestCore.NativeQuestType,
                questFlavor = StoryQuestCore.NativeQuestType,
                questText = LabelCore.MakeId(text), // smuggled raw text (unused by our postfix, but safe if read)
                questGiver = null,
                hasQuestGiver = false,
                questEnder = null,
                hasQuestEnder = false,
                questTarget1 = target,
                killedOnQuest = new bool[10],
                angeredOnQuest = new bool[10],
                playerTally = new int[10],
                itemsDestroyed = new int[10],
            };
            gc.quests.mainQuestList.Add(q);
            gc.quests.unchangingQuestList.Add(q);
            int slot = gc.quests.AddQuest(gc.playerAgent, q); // Accepts + assigns a sheet slot
            if (slot < 0)
            {
                // No free slot — keep it out of the sheet but don't leak it.
                gc.quests.mainQuestList.Remove(q);
                gc.quests.unchangingQuestList.Remove(q);
                return null;
            }
            q.questSlot = gc.quests.questSheet.questSlot[slot];
            q.questSlotNum = slot;
            return q;
        }

        // ---- completion / teardown -----------------------------------------

        /// <summary>Force-complete a story quest (a character's "nice, you did
        /// it").</summary>
        internal static string Done(string idRaw)
        {
            var id = StoryQuestCore.NormalizeId(idRaw);
            if (!_quests.TryGetValue(id, out var e))
                return $"no story quest '{id}'";
            Complete(e, true, "forced");
            return $"story quest '{id}' completed";
        }

        internal static string Clear(string idRaw)
        {
            var id = StoryQuestCore.NormalizeId(idRaw);
            if (!_quests.TryGetValue(id, out var e))
                return $"no story quest '{id}'";
            Teardown(e);
            _quests.Remove(id);
            return $"story quest '{id}' cleared";
        }

        internal static int ClearAll()
        {
            int n = _quests.Count;
            foreach (var e in new List<Entry>(_quests.Values))
                Teardown(e);
            _quests.Clear();
            return n;
        }

        internal static string ListJson()
        {
            var arr = new JArray();
            foreach (var e in _quests.Values)
            {
                arr.Add(new JObject
                {
                    ["id"] = e.Id,
                    ["type"] = StoryQuestCore.TypeName(e.Type),
                    ["text"] = e.Text,
                    ["target"] = e.HasEntity ? (JToken)e.TargetUid : null,
                    ["x"] = Mathf.Round(TargetPos(e).x * 100f) / 100f,
                    ["y"] = Mathf.Round(TargetPos(e).y * 100f) / 100f,
                    ["nativeRow"] = e.Native != null,
                });
            }
            return arr.ToString(Formatting.None);
        }

        /// <summary>Complete: broadcast the event, tear down marker + native row.</summary>
        private static void Complete(Entry e, bool success, string reason)
        {
            Broadcast(e, success ? "complete" : "failed", reason);
            Teardown(e);
            _quests.Remove(e.Id);
        }

        private static void Teardown(Entry e)
        {
            if (e.HasMarker)
                try { Labels.Clear(e.TargetUid); } catch { }
            if (e.Native != null)
                RemoveNative(e.Native);
            e.Native = null;
        }

        private static void RemoveNative(Quest q)
        {
            try
            {
                var gc = GameController.gameController;
                if (gc == null || gc.quests == null)
                    return;
                // Mark Done so the vanilla sheet dims/drops it cleanly.
                try { gc.quests.SetQuestProgress(q, "Done"); } catch { }
                gc.quests.mainQuestList.Remove(q);
                gc.quests.unchangingQuestList.Remove(q);
                if (gc.playerAgent != null && gc.playerAgent.myQuests != null)
                    gc.playerAgent.myQuests.Remove(q);
                var sheet = gc.quests.questSheet;
                if (sheet != null && sheet.questSlot != null)
                    foreach (var slot in sheet.questSlot)
                        if (slot != null && slot.myQuest == q)
                            slot.myQuest = null;
            }
            catch (Exception ex)
            {
                EightPlayersPlugin.Log.LogWarning($"story quest native teardown: {ex.Message}");
            }
        }

        private static void Broadcast(Entry e, string status, string reason)
        {
            try
            {
                HttpChannel.Broadcast(new JObject
                {
                    ["event"] = "quest_complete",
                    ["id"] = e.Id,
                    ["type"] = StoryQuestCore.TypeName(e.Type),
                    ["status"] = status,          // "complete" | "failed"
                    ["reason"] = reason,          // "kill" | "reach" | "interact" | "forced" | "target_died"
                    ["target"] = e.HasEntity ? (JToken)e.TargetUid : null,
                });
            }
            catch (Exception ex)
            {
                EightPlayersPlugin.Log.LogWarning($"quest_complete broadcast failed: {ex.Message}");
            }
        }

        // ---- polled watcher (Plugin.Update ~3Hz) ---------------------------

        internal static void Tick()
        {
            if (_quests.Count == 0)
            {
                SyncLevelKey();
                return;
            }
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.34f;

            // Level churned (or game gone): uids are meaningless now — drop all.
            if (LevelChanged())
            {
                ClearAll();
                return;
            }

            foreach (var e in new List<Entry>(_quests.Values))
            {
                try { CheckOne(e); }
                catch (Exception ex) { EightPlayersPlugin.Log.LogWarning($"story quest tick '{e.Id}': {ex.Message}"); }
            }
        }

        private static void CheckOne(Entry e)
        {
            // Entity target that despawned → the goal is gone; drop quietly.
            Agent agent = e.TargetUid != 0 ? GameStateApi.FindAgent(e.TargetUid) : null;
            PlayfieldObject obj = agent != null ? (PlayfieldObject)agent
                : (e.TargetUid != 0 ? GameStateApi.FindObjectReal(e.TargetUid) : null);
            if (e.HasEntity && obj == null && agent == null)
            {
                // target vanished — for protect that's a failure; else silent drop
                if (e.Type == StoryQuestType.Protect)
                    Complete(e, false, "target_died");
                else
                    Complete(e, true, "target_gone");
                return;
            }

            switch (e.Type)
            {
                case StoryQuestType.Kill:
                    if (agent != null && agent.dead && !agent.resurrect && !agent.teleporting)
                        Complete(e, true, "kill");
                    break;
                case StoryQuestType.Protect:
                    if (agent != null && agent.dead && !agent.resurrect && !agent.teleporting)
                        Complete(e, false, "target_died");
                    break;
                case StoryQuestType.Reach:
                case StoryQuestType.Interact:
                    float d = ClosestPlayerDistance(TargetPos(e, obj));
                    if (d >= 0f && StoryQuestCore.ReachedByDistance(e.Type, d))
                        Complete(e, true, StoryQuestCore.TypeName(e.Type));
                    break;
            }
        }

        private static Vector2 TargetPos(Entry e, PlayfieldObject live = null)
        {
            if (live != null && live.tr != null)
                return live.tr.position;
            return e.Pos;
        }

        private static float ClosestPlayerDistance(Vector2 pos)
        {
            var gc = GameController.gameController;
            if (gc == null)
                return -1f;
            float best = -1f;
            foreach (var p in gc.playerAgentList)
            {
                if (p == null || p.dead || p.tr == null)
                    continue;
                float d = ((Vector2)p.tr.position - pos).magnitude;
                if (best < 0f || d < best)
                    best = d;
            }
            return best;
        }

        // ---- level-change bookkeeping --------------------------------------

        private static int CurrentLevelKey()
        {
            var gc = GameController.gameController;
            if (gc == null || gc.loadLevel == null || gc.sessionDataBig == null || !gc.loadCompleteReally)
                return int.MinValue;
            return gc.loadLevel.randomSeedNum * 1000 + gc.sessionDataBig.curLevel;
        }

        private static void SyncLevelKey() => _lastLevelKey = CurrentLevelKey();

        private static bool LevelChanged()
        {
            int key = CurrentLevelKey();
            if (key != _lastLevelKey)
            {
                _lastLevelKey = key;
                return true;
            }
            return false;
        }
    }

    // Draw our raw objective text into the native mission-sheet row. The vanilla
    // QuestSlot.UpdateQuest switch has no case for the "EPStory" questType, so it
    // leaves the row's instruction line untouched — we set it here, and hide the
    // secondary instruction lines a multi-part native quest would use.
    [HarmonyPatch(typeof(QuestSlot), nameof(QuestSlot.UpdateQuest))]
    internal static class StoryQuest_QuestSlot_Patch
    {
        private static void Postfix(QuestSlot __instance)
        {
            try
            {
                var q = __instance.quest;
                if (q == null || q.questType != StoryQuestCore.NativeQuestType)
                    return;
                var text = LabelCore.LabelText(q.questText) ?? q.questText;
                if (__instance.questInstruction1 != null)
                {
                    __instance.questInstruction1.text = text;
                    __instance.questInstruction1.enabled = true;
                }
                if (__instance.questInstruction2 != null) __instance.questInstruction2.enabled = false;
                if (__instance.questInstruction3 != null) __instance.questInstruction3.enabled = false;
            }
            catch { /* never let a UI postfix break the mission sheet */ }
        }
    }
}
