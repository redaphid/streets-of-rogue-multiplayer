using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace EightPlayers
{
    // Player-action feed (the game's own CLASSIFIED player-action log).
    //
    // Streets of Rogue awards "skill points" for every notable thing a player
    // does — killing a Cop, offing an Innocent, arresting, stealing, hacking,
    // destroying property, completing a mission — and pops the running
    // upper-right "+N" notification. Every one of those funnels through the
    // single method SkillPoints.AddPoints(string pointsType, int extraNum),
    // which is ALREADY player-gated (it returns immediately unless the agent
    // that earned the points is a player). That makes it the cleanest place to
    // observe "a player just did a classified action": one hook, the game's own
    // taxonomy (the pointsType string), no guessing.
    //
    // A Prefix mirrors the game's own player gate and pushes a `player_action`
    // frame on the existing /events stream. Runs on the Unity main thread (the
    // game only calls AddPoints from gameplay code), so Broadcast is safe.
    //
    // Frame shape (matches HttpChannel's other NDJSON frames):
    //   {"event":"player_action",
    //    "text":  "KillPointsInnocent",   // raw pointsType — the game's label
    //    "kind":  "kill",                 // coarse bucket (kill/knockout/arrest/
    //                                     //   theft/tamper/destruction/mission/
    //                                     //   bonus/other)
    //    "targetType": "Innocent",        // OPTIONAL — present only when the
    //                                     //   pointsType names a victim class
    //                                     //   (Innocent / Rival / Robot)
    //    "player": 1,                     // which player (Agent.isPlayer, 1..4)
    //    "points": 1}                     // extraNum passed to AddPoints
    //
    // Note (multiplayer): on a networked host, a REMOTE player's points are
    // RPC'd to that client and displayed there — AddPoints on the host returns
    // before the switch. We fire for whichever instance actually reaches the
    // point-award path, i.e. the player who owns the action, which is what a
    // signal consumer wants. In the solo/GM launch every player is local.
    [HarmonyPatch(typeof(SkillPoints), "AddPoints", new[] { typeof(string), typeof(int) })]
    internal static class PlayerAction_Patch
    {
        // SkillPoints.agent is private; cache the accessor once.
        private static readonly FieldInfo AgentField = AccessTools.Field(typeof(SkillPoints), "agent");

        private static void Prefix(SkillPoints __instance, string pointsType, int extraNum)
        {
            try
            {
                var agent = AgentField.GetValue(__instance) as Agent;
                // Mirror the game's own gate: only players earn skill points.
                if (agent == null || agent.isPlayer <= 0)
                    return;

                var frame = new JObject
                {
                    ["event"] = "player_action",
                    ["text"] = pointsType,
                    ["kind"] = Classify(pointsType),
                    ["player"] = agent.isPlayer,
                    ["points"] = extraNum,
                };
                var target = TargetType(pointsType);
                if (target != null)
                    frame["targetType"] = target;

                HttpChannel.Broadcast(frame);
            }
            catch
            {
                // Never let the signal feed disturb scoring / the game loop.
            }
        }

        // Coarse action bucket derived from the game's pointsType label.
        private static string Classify(string t)
        {
            if (t == null)
                return "other";
            if (t.Contains("KnockOut"))
                return "knockout";
            if (t.Contains("Arrest"))
                return "arrest";
            if (t.Contains("Kill"))                 // KillPoints*, IndirectlyKill*, KilledRobot
                return "kill";
            if (t.Contains("Steal") || t.Contains("Pickpocket") || t.Contains("Shakedown"))
                return "theft";
            if (t.Contains("Hack") || t.Contains("Lockpick") || t.Contains("Tamper")
                || t.Contains("Unlock") || t.Contains("Disarm") || t.Contains("RemoveWindow")
                || t.Contains("RemoveSlaveHelmet") || t.Contains("PoisonAir"))
                return "tamper";
            if (t.Contains("Destruction") || t.Contains("FireExtinguish"))
                return "destruction";
            if (t.Contains("Mission") || t.Contains("Election") || t.Contains("Won"))
                return "mission";
            if (t.Contains("Bonus") || t.Contains("Treasure") || t.Contains("Freed")
                || t.Contains("Enslaved") || t.Contains("Joke") || t.Contains("Winner"))
                return "bonus";
            return "other";
        }

        // Victim class the pointsType names, if any (surfaced as targetType).
        private static string TargetType(string t)
        {
            if (t == null)
                return null;
            if (t.Contains("Innocent"))
                return "Innocent";
            if (t.Contains("Rival"))
                return "Rival";
            if (t.Contains("Robot"))
                return "Robot";
            return null;
        }
    }
}
