using System;

namespace EightPlayers
{
    // Pure logic for GM/character STORY QUESTS (rogue-gm issue #22 / #7) — no
    // Unity/game deps, unit-tested by EightPlayers.Tests. The game-facing half
    // (native Quest registration, the marker, the polled completion tick, the
    // quest_complete broadcast) lives in StoryQuest.cs.
    //
    // A story quest turns a concrete goal a character hands the player ("knock
    // over the cart", "meet Slim", "kill the Bouncer") into something the game
    // renders like its OWN per-map quest: an ALL-CAPS marker over the target
    // (the same QuestMarker path Label.cs rides) plus a native mission-sheet row.
    // Completion is watched here and fired back to the GM as a quest_complete
    // event — the signal the GM never had before (issue #7).
    //
    // Four completion behaviors, mapped to the nearest sensible native idea:
    //   reach     — a player gets close to the target/spot (native quests have
    //               no clean single-target "reach", so this is polled).
    //   kill      — the target agent dies (native "Kill" semantics).
    //   interact  — a player gets to interaction range of the target (polled
    //               proxy for "use it / talk to it / knock it over").
    //   protect   — the target must survive; completes only on `quest done`
    //               (and reports failure if the target dies first).
    internal enum StoryQuestType { Reach, Kill, Interact, Protect }

    internal static class StoryQuestCore
    {
        /// <summary>Sentinel questType stamped on the native Quest we register.
        /// Deliberately NOT a vanilla type so the game's QuestUpdate completion
        /// state machine and QuestSlot renderer both fall through — our polled
        /// tick drives completion and a QuestSlot postfix draws the raw text.</summary>
        internal const string NativeQuestType = "EPStory";

        /// <summary>Objective text max — matches the label budget (readable over
        /// a head AND in the narrow mission-sheet row).</summary>
        internal const int MaxTextChars = LabelCore.MaxChars;

        internal const int MaxIdChars = 32;

        /// <summary>How close (world units) a player must get for reach/interact
        /// completion. Interact is tighter — roughly SoR's own interaction reach.</summary>
        internal const float ReachRadius = 2.5f;
        internal const float InteractRadius = 1.4f;

        /// <summary>How close a player must be to a protect target's death for
        /// the quest to report FAILURE. A death the player was around to see is
        /// a real story beat; one across the map is noise — the 2026-07-11
        /// playtest flashed "QUEST FAILED" for ambient chaos the player never
        /// witnessed, which reads as a bug. Off-screen deaths cancel silently.</summary>
        internal const float ProtectWitnessRadius = 20f;

        /// <summary>Was a protect target's death witnessed (close enough to a
        /// live player to be a fair failure)? closestPlayerDist &lt; 0 means no
        /// live player could be measured — treat as unwitnessed.</summary>
        internal static bool ProtectFailureIsWitnessed(float closestPlayerDist) =>
            closestPlayerDist >= 0f && closestPlayerDist <= ProtectWitnessRadius;

        /// <summary>Parse a TYPE token (case-insensitive) to the enum; throws a
        /// friendly error listing the choices.</summary>
        internal static StoryQuestType ParseType(string type)
        {
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "reach": return StoryQuestType.Reach;
                case "kill": return StoryQuestType.Kill;
                case "interact": return StoryQuestType.Interact;
                case "protect": return StoryQuestType.Protect;
                default:
                    throw new ArgumentException(
                        $"unknown quest TYPE '{type}' — use reach | kill | interact | protect");
            }
        }

        /// <summary>Canonical lower-case name (for JSON listings / replies).</summary>
        internal static string TypeName(StoryQuestType t)
        {
            switch (t)
            {
                case StoryQuestType.Reach: return "reach";
                case StoryQuestType.Kill: return "kill";
                case StoryQuestType.Interact: return "interact";
                case StoryQuestType.Protect: return "protect";
                default: return "reach";
            }
        }

        /// <summary>reach/interact complete by proximity; kill by death; protect
        /// only on an explicit `done`.</summary>
        internal static bool IsProximityType(StoryQuestType t) =>
            t == StoryQuestType.Reach || t == StoryQuestType.Interact;

        /// <summary>Completion radius for proximity types, else 0.</summary>
        internal static float Radius(StoryQuestType t) =>
            t == StoryQuestType.Reach ? ReachRadius
            : t == StoryQuestType.Interact ? InteractRadius
            : 0f;

        /// <summary>Whether a proximity-type quest is complete given the closest
        /// player's distance to the target. Pure so it's unit-testable.</summary>
        internal static bool ReachedByDistance(StoryQuestType t, float distance) =>
            IsProximityType(t) && distance >= 0f && distance <= Radius(t);

        /// <summary>Trim + validate a quest id (a short handle the GM/character
        /// picks so it can `quest done <id>` later). No spaces (it's one token).</summary>
        internal static string NormalizeId(string id)
        {
            var s = (id ?? "").Trim();
            if (s.Length == 0)
                throw new ArgumentException("empty quest id");
            if (s.Length > MaxIdChars)
                throw new ArgumentException($"quest id too long ({s.Length} chars, max {MaxIdChars}): \"{s}\"");
            if (s.IndexOf(' ') >= 0)
                throw new ArgumentException($"quest id must be a single token (no spaces): \"{s}\"");
            return s;
        }

        /// <summary>Trim + validate objective text (reuses the label budget;
        /// case preserved — vanilla style is ALL CAPS but that's the caller's
        /// choice).</summary>
        internal static string NormalizeText(string text) => LabelCore.Normalize(text);
    }
}
