using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers.Tracing
{
    // Trace patches for the vanilla behavior baseline (see docs/ecs-netcode.md).
    // Each patch targets the master overload every thinner overload funnels
    // into, per the choke-point survey of the decompiled code:
    //   agent  : SpawnerMain.SpawnAgent, StatusEffects.ChangeHealth/SetupDeath
    //   inv    : InvDatabase.AddItem/DropItem
    //   door   : Door.OpenDoor
    //   level  : LoadLevel.SetupBasicLevel (seed already resolved by then)
    //   move   : Movement.PlayerMovement (sampled)
    //   net    : every weaved UserCode_Cmd*/Rpc* body on the ObjectMult* hubs
    //            (installed reflectively by NetTrace.Install — the game has no
    //            SyncVars; these methods ARE its sync protocol)

    internal static class TraceFmt
    {
        public static JObject AgentRef(Agent a)
        {
            if (a == null)
                return null;
            return new JObject
            {
                ["uid"] = a.UID,
                ["name"] = string.IsNullOrEmpty(a.agentRealName) ? a.agentName : a.agentRealName,
                ["player"] = a.isPlayer,
            };
        }

        public static JObject ObjRef(PlayfieldObject o)
        {
            if (o == null)
                return null;
            if (o is Agent a)
                return AgentRef(a);
            return new JObject { ["uid"] = o.UID, ["type"] = o.GetType().Name };
        }

        public static JArray Vec(Vector3 v) => new JArray(Math.Round(v.x, 2), Math.Round(v.y, 2));

        public static string Arg(object value)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return s.Length > 48 ? s.Substring(0, 48) : s;
                case Vector2 v: return $"({v.x:0.##},{v.y:0.##})";
                case Vector3 v: return $"({v.x:0.##},{v.y:0.##})";
                case Agent a: return $"agent:{a.UID}";
                case PlayfieldObject p: return $"{p.GetType().Name}:{p.UID}";
                case bool _:
                case byte _:
                case int _:
                case uint _:
                case float _:
                    return value.ToString();
                default:
                    return value.GetType().Name;
            }
        }
    }

    [HarmonyPatch]
    internal static class TraceSpawnAgent_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(SpawnerMain))
                .Where(m => m.Name == "SpawnAgent")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(Agent __result, Vector3 agentPos, string agentType, string spawnType, int playerColor)
        {
            if (!Trace.Enabled)
                return;
            Trace.Emit("agent", "spawn", new JObject
            {
                ["agent"] = TraceFmt.AgentRef(__result),
                ["agentType"] = agentType,
                ["spawnType"] = spawnType,
                ["playerColor"] = playerColor,
                ["pos"] = TraceFmt.Vec(agentPos),
            });
        }
    }

    [HarmonyPatch]
    internal static class TraceChangeHealth_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(StatusEffects))
                .Where(m => m.Name == "ChangeHealth")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(StatusEffects __instance, float healthNum, PlayfieldObject damagerObject)
        {
            if (!Trace.Enabled)
                return;
            Trace.Emit("agent", "health", new JObject
            {
                ["agent"] = TraceFmt.AgentRef(__instance.agent),
                ["delta"] = Math.Round(healthNum, 2),
                ["health"] = __instance.agent != null ? Math.Round(__instance.agent.health, 2) : 0,
                ["by"] = TraceFmt.ObjRef(damagerObject),
            });
        }
    }

    [HarmonyPatch]
    internal static class TraceSetupDeath_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(StatusEffects))
                .Where(m => m.Name == "SetupDeath")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(StatusEffects __instance, PlayfieldObject damagerObject, bool killedOnClient)
        {
            if (!Trace.Enabled)
                return;
            Trace.Emit("agent", "death", new JObject
            {
                ["agent"] = TraceFmt.AgentRef(__instance.agent),
                ["by"] = TraceFmt.ObjRef(damagerObject),
                ["onClient"] = killedOnClient,
            });
        }
    }

    [HarmonyPatch]
    internal static class TraceAddItem_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(InvDatabase))
                .Where(m => m.Name == "AddItem")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(InvDatabase __instance, string itemName, int itemCount)
        {
            if (!Trace.Enabled)
                return;
            Trace.Emit("inv", "add", new JObject
            {
                ["agent"] = TraceFmt.AgentRef(__instance.agent),
                ["item"] = itemName,
                ["count"] = itemCount,
            });
        }
    }

    [HarmonyPatch]
    internal static class TraceDropItem_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(InvDatabase))
                .Where(m => m.Name == "DropItem")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(InvDatabase __instance, InvItem item)
        {
            if (!Trace.Enabled)
                return;
            Trace.Emit("inv", "drop", new JObject
            {
                ["agent"] = TraceFmt.AgentRef(__instance.agent),
                ["item"] = item?.invItemName,
            });
        }
    }

    [HarmonyPatch(typeof(Door), "OpenDoor", typeof(Agent), typeof(bool))]
    internal static class TraceOpenDoor_Patch
    {
        private static void Postfix(Door __instance, Agent myAgent, bool remote)
        {
            if (!Trace.Enabled)
                return;
            Trace.Emit("door", "open", new JObject
            {
                ["door"] = __instance.UID,
                ["doorType"] = __instance.doorType,
                ["agent"] = TraceFmt.AgentRef(myAgent),
                ["remote"] = remote,
            });
        }
    }

    // SetupMore2 is on the always-run generation chain (SetupBasicLevel is
    // only reached for special map shapes) and the seed is resolved before it.
    [HarmonyPatch(typeof(LoadLevel), "SetupMore2")]
    internal static class TraceLevelSeed_Patch
    {
        private static void Prefix(LoadLevel __instance)
        {
            if (!Trace.Enabled)
                return;
            var gc = GameController.gameController;
            Trace.Emit("level", "generate", new JObject
            {
                ["seedNum"] = __instance.randomSeedNum,
                ["seedLetter"] = __instance.randomSeedLetter,
                ["levelType"] = gc != null ? gc.levelType : null,
                ["curLevel"] = gc != null ? (int?)gc.sessionDataBig.curLevel : null,
            });
        }
    }

    [HarmonyPatch(typeof(LoadLevel), "IncreaseLevel")]
    internal static class TraceLevelAdvance_Patch
    {
        private static void Postfix()
        {
            if (!Trace.Enabled)
                return;
            var gc = GameController.gameController;
            Trace.Emit("level", "advance", new JObject
            {
                ["curLevel"] = gc.sessionDataBig.curLevel,
                ["endless"] = gc.sessionDataBig.curLevelEndless,
                ["actual"] = gc.sessionDataBig.curLevelActual,
            });
        }
    }

    // Local input becoming motion, sampled: one event per agent per SampleEvery
    // fixed steps (50 Hz physics -> ~2 events/s/player). The full-rate signal
    // for parity diffing is positions, not forces, so sampling is fine.
    [HarmonyPatch(typeof(Movement), "PlayerMovement")]
    internal static class TracePlayerMovement_Patch
    {
        private const int SampleEvery = 25;
        private static readonly Dictionary<int, int> Counters = new Dictionary<int, int>();

        private static void Postfix(Agent ___agent)
        {
            if (!Trace.Enabled || ___agent == null)
                return;
            Counters.TryGetValue(___agent.UID, out var n);
            Counters[___agent.UID] = n + 1;
            if (n % SampleEvery != 0)
                return;
            Trace.Emit("move", "sample", new JObject
            {
                ["agent"] = TraceFmt.AgentRef(___agent),
                ["pos"] = TraceFmt.Vec(___agent.tr.position),
            });
        }
    }

    // Reflective tracer for the game's entire manual sync protocol: every
    // weaved UserCode_Cmd* / UserCode_Rpc* / UserCode_Target* body on the
    // ObjectMult* hubs (~400+ methods). Installed only when tracing is on —
    // patching this many methods costs a few seconds of startup.
    internal static class NetTrace
    {
        private static readonly string[] HubTypeNames =
        {
            "ObjectMult", "ObjectMultAgent", "ObjectMultItem",
            "ObjectMultObject", "ObjectMultPlayfield", "ObjectMultFire",
        };

        public static void Install(Harmony harmony)
        {
            if (!Trace.Enabled)
                return;
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(NetTrace), nameof(Prefix)));
            int patched = 0, failed = 0;
            foreach (var typeName in HubTypeNames)
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                    continue;
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                {
                    if (!method.Name.StartsWith("UserCode_", StringComparison.Ordinal))
                        continue;
                    try
                    {
                        harmony.Patch(method, prefix: prefix);
                        patched++;
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }
            }
            EightPlayersPlugin.Log.LogInfo($"TRACE net hooks installed on {patched} Cmd/Rpc bodies ({failed} failed)");
        }

        private static void Prefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (!Trace.Enabled)
                return;
            // UserCode_CmdSpawnAgent__Vector2__UInt32__String__String -> CmdSpawnAgent
            var name = __originalMethod.Name.Substring("UserCode_".Length);
            var sep = name.IndexOf("__", StringComparison.Ordinal);
            if (sep > 0)
                name = name.Substring(0, sep);

            var args = new JArray();
            if (__args != null)
                for (int i = 0; i < __args.Length && i < 8; i++)
                    args.Add(TraceFmt.Arg(__args[i]));

            Trace.Emit("net", "call", new JObject
            {
                ["hub"] = __instance?.GetType().Name,
                ["method"] = name,
                ["args"] = args,
            });
        }
    }
}
