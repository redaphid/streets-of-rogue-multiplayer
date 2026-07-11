using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EightPlayers
{
    // GM labels (rogue-gm issue #17): pin quest-marker-style world-space text
    // (PROTECT / TRAITOR / TALK / BOSS...) over ANY agent or object, via the
    // game's own QuestMarker system — the exact rendering vanilla uses for
    // RESCUE / NEUTRALIZE over quest targets.
    //
    // Vanilla path being reused (mapped from Quests.CreateQuestMarker):
    //   creation  Quests.CreateQuestMarker(target, quest, markerType[, homeBase])
    //             instantiates the QuestMarker prefab AS A CHILD of the target
    //             (so it follows and dies with it), plus two QuestMarkerSmall
    //             minimap icons and — for text-bearing types — a QuestMarkerText
    //             under WorldSpaceGUI.questMarkerTextNest.
    //   text      QuestMarkerText.StartReal: for markers with a non-empty
    //             homeBaseMarkerType, myText.text = nameDB.GetName(
    //             homeBaseMarkerType, "Interface") — a NameDB id, NOT raw text.
    //   why "HomeBaseMarker"  it is the ONLY marker type that is always visible
    //             (text alpha forced to 1 every LateUpdate), quest-free (no
    //             QuestUpdate state machine to fight), and carries its own
    //             arbitrary id string. Quest-target markers need a live Quest
    //             and hide until accepted; NonQuestAgent markers auto-manage
    //             text/visibility from hardcoded agent names.
    //   follow    QuestMarker.LateUpdate repositions above myObject's sprite;
    //             QuestMarkerText.LateUpdate tracks playfieldObject each frame.
    //   cleanup   markers are children of the target — despawn destroys them
    //             (OnDestroy tears down the text + minimap pieces and leaves
    //             gc.questMarkerList). A DEAD-but-lingering agent keeps its
    //             corpse though, so Labels.Tick prunes labels off dead agents
    //             with vanilla's own death test (dead && !teleporting &&
    //             !resurrect — the nonQuestAgent branch's condition).
    //
    // The arbitrary text is smuggled past NameDB (which throws on unknown ids)
    // by prefixing it "EPLABEL::" and short-circuiting NameDB.GetName — the
    // same trick DialogueMenu uses for custom menu options.

    [HarmonyPatch(typeof(NameDB), nameof(NameDB.GetName))]
    internal static class Label_NameDB_Patch
    {
        private static bool Prefix(string myName, ref string __result)
        {
            var text = LabelCore.LabelText(myName);
            if (text == null)
                return true;
            __result = text;
            return false;
        }
    }

    internal static class Labels
    {
        // target UID -> its live label marker (+ text for listings). Main
        // thread only. Uids churn per level; Tick/Prune drop dead entries.
        private static readonly Dictionary<int, QuestMarker> _markers = new Dictionary<int, QuestMarker>();
        private static readonly Dictionary<int, string> _texts = new Dictionary<int, string>();

        /// <summary>Pin (or replace) a label over an agent OR an ObjectReal by
        /// uid. Throws with a friendly message when the uid matches nothing.</summary>
        internal static string Apply(int uid, string text)
        {
            var normalized = LabelCore.Normalize(text);
            var gc = GameController.gameController;
            if (gc == null || gc.quests == null)
                throw new InvalidOperationException("no game running");
            PlayfieldObject target = GameStateApi.FindAgent(uid);
            if (target == null)
                target = GameStateApi.FindObjectReal(uid);
            if (target == null)
                throw new ArgumentException($"no agent or object with uid {uid}");
            var agent = target as Agent;
            if (agent != null && agent.dead)
                throw new ArgumentException($"agent {uid} is dead — labels only stick to the living");

            Clear(uid);   // replace-in-place
            Prune();      // opportunistic: drop entries whose target despawned

            // The home-base-marker path parents the marker to the target and
            // wires an always-visible QuestMarkerText whose text comes from
            // our smuggled NameDB id.
            gc.quests.CreateQuestMarker(target, null, "HomeBaseMarker", LabelCore.MakeId(normalized));
            var marker = target.homeBaseMarker;
            if (marker == null)
                throw new InvalidOperationException("quest marker did not attach (CreateQuestMarker returned nothing)");
            _markers[uid] = marker;
            _texts[uid] = normalized;
            var name = agent != null
                ? (string.IsNullOrEmpty(agent.agentRealName) ? agent.agentName : agent.agentRealName)
                : target.objectName;
            return $"label ON: \"{normalized}\" pinned over uid {uid} ('{name}') — follows the target, auto-clears on death/despawn; uids churn per level, re-label after level changes";
        }

        /// <summary>Remove one label (destroys its marker). Returns a reply line.</summary>
        internal static string Clear(int uid) =>
            Remove(uid) ? $"label cleared on uid {uid}" : $"no label on uid {uid}";

        internal static int ClearAll()
        {
            int n = 0;
            foreach (var uid in new List<int>(_markers.Keys))
                if (Remove(uid))
                    n++;
            return n;
        }

        internal static string Summary()
        {
            Prune();
            if (_markers.Count == 0)
                return "no labels";
            var lines = new List<string>();
            foreach (var kv in _markers)
                lines.Add($"  uid={kv.Key} \"{(_texts.TryGetValue(kv.Key, out var t) ? t : "?")}\"");
            lines.Add($"labels: {_markers.Count}");
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>Self-cleaning, called from Plugin.Update (~1Hz): labels on
        /// despawned targets vanish with them (marker is a child — just forget
        /// the entry); labels on DEAD agents are actively destroyed, using the
        /// same death test vanilla's nonQuestAgent markers use.</summary>
        internal static void Tick()
        {
            if (_markers.Count == 0 || Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 1f;
            List<int> deadTargets = null;
            foreach (var kv in _markers)
            {
                var m = kv.Value;
                if (m == null || !m.reallyStarted)
                    continue; // destroyed entries are swept by Prune below
                var agent = m.myObject as Agent;
                if (agent != null && agent.dead && !agent.teleporting && !agent.resurrect)
                    (deadTargets ?? (deadTargets = new List<int>())).Add(kv.Key);
            }
            if (deadTargets != null)
                foreach (var uid in deadTargets)
                    Remove(uid);
            Prune();
        }

        private static float _next;

        private static bool Remove(int uid)
        {
            if (!_markers.TryGetValue(uid, out var marker))
                return false;
            _markers.Remove(uid);
            _texts.Remove(uid);
            if (marker != null)
            {
                // vanilla never resets homeBaseMarker on destroy; do it so a
                // later re-label reads a clean slate.
                if (marker.myObject != null && marker.myObject.homeBaseMarker == marker)
                    marker.myObject.homeBaseMarker = null;
                marker.DestroyMe(); // handles both streaming (pool) and normal (Destroy)
            }
            return true;
        }

        /// <summary>Forget entries whose marker Unity-died (target despawned or
        /// level changed — the marker is a child of the target).</summary>
        private static void Prune()
        {
            List<int> stale = null;
            foreach (var kv in _markers)
                if (kv.Value == null)
                    (stale ?? (stale = new List<int>())).Add(kv.Key);
            if (stale == null)
                return;
            foreach (var uid in stale)
            {
                _markers.Remove(uid);
                _texts.Remove(uid);
            }
        }
    }
}
