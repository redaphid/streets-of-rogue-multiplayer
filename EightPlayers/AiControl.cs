using System.Collections.Generic;
using UnityEngine;

namespace EightPlayers
{
    // Central registry of agents the mod is actively PUPPETING or PORTRAYING —
    // spawned character-daemon bodies, GM-fed avatars, recruited allies, and
    // Lua-behavior-driven NPCs. It exists to fix two live-session glitches
    // (see docs/ops-logs the-unwritten-2026-07-14 §8a/§8b):
    //
    //   §8a  The game fires stock chatter (Agent.SayDialogue on its own
    //        AI/timer, sourced from a shared type-keyed dialogue DB — NOT the
    //        per-agent myDialogueList, which is empty on our bodies) on every
    //        agent, so a controlled Comedian still tells its canned jokes and a
    //        controlled Cop says cop lines between the words we give it. There
    //        is no per-agent mute boolean in the game. DialogueMenu_Mute_Patch
    //        suppresses SayDialogue for any uid IsControlled here, so a
    //        controlled character speaks ONLY via `say`.
    //
    //   §8b  A killed character daemon (or any level transition) used to leave
    //        its body's menu FLAGGED-but-empty; uids churn per floor, so the
    //        flag — and the "(one moment...)" placeholder — landed on whatever
    //        random NPC inherited that uid. The reaper below wipes both the
    //        control set and the menu registry on level change, and drops any
    //        tracked uid whose body has died or despawned.
    //
    // Main-thread only (verbs, Harmony hooks, and Plugin.Update all run on
    // Unity's main thread — no locking needed).
    internal static class AiControl
    {
        private static readonly HashSet<int> _controlled = new HashSet<int>();

        /// <summary>Flag an agent as mod-controlled (mutes its stock chatter).
        /// Called by the control-taking verbs: aimarker on / recruit / behavior /
        /// aicontrol on. Idempotent.</summary>
        internal static void Mark(int uid)
        {
            if (uid != 0)
                _controlled.Add(uid);
        }

        /// <summary>Release control (restores stock chatter). Called by aimarker
        /// off / clearbehavior / aicontrol off, and by the reaper on death.</summary>
        internal static void Unmark(int uid) => _controlled.Remove(uid);

        internal static bool IsControlled(int uid) => _controlled.Contains(uid);

        internal static int Count => _controlled.Count;

        internal static int ClearAll()
        {
            int n = _controlled.Count;
            _controlled.Clear();
            return n;
        }

        // ---- level / death reaper (§8b) — driven from Plugin.Update ----------

        private static int _lastLevelKey = int.MinValue;
        private static float _nextReap;

        /// <summary>Same (seed, level) fingerprint StoryQuests uses to notice a
        /// floor change. int.MinValue while no level is loaded.</summary>
        private static int LevelKey()
        {
            var gc = GameController.gameController;
            if (gc == null || gc.loadLevel == null || gc.sessionDataBig == null)
                return int.MinValue;
            return gc.loadLevel.randomSeedNum * 1000 + gc.sessionDataBig.curLevel;
        }

        internal static void Tick()
        {
            int key = LevelKey();
            if (key != _lastLevelKey)
            {
                _lastLevelKey = key;
                // Uids are meaningless across levels — wipe BOTH registries so a
                // recycled uid never inherits a stale control flag or menu flag
                // ("(one moment...)" haunting a random NPC on the next floor).
                _controlled.Clear();
                DialogueMenuCore.ClearAll();
                return;
            }
            if (Time.unscaledTime < _nextReap)
                return;
            _nextReap = Time.unscaledTime + 0.5f;
            ReapDead();
        }

        /// <summary>Drop control + menu flag for any tracked uid whose body is
        /// dead or gone (a killed daemon leaves its menu flagged-but-empty).</summary>
        private static void ReapDead()
        {
            List<int> gone = null;
            // Union of control-flagged and menu-flagged uids — either can outlive
            // its body.
            var uids = new HashSet<int>(_controlled);
            foreach (var uid in DialogueMenuCore.FlaggedUids())
                uids.Add(uid);
            foreach (var uid in uids)
            {
                var a = GameStateApi.FindAgent(uid);
                if (a == null || a.dead)
                {
                    if (gone == null)
                        gone = new List<int>();
                    gone.Add(uid);
                }
            }
            if (gone == null)
                return;
            foreach (var uid in gone)
            {
                _controlled.Remove(uid);
                DialogueMenuCore.Clear(uid);
            }
        }
    }
}
