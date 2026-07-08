using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EightPlayers.EcsNet
{
    // Event-driven ECS publishing: the same vanilla choke points the trace
    // layer observes (docs/trace-choke-points.md) notify the net manager so
    // component updates go out when state changes, not by polling. Health is
    // the first system ported this way.
    [HarmonyPatch]
    internal static class EcsHealthHook_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(StatusEffects))
                .Where(m => m.Name == "ChangeHealth")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(StatusEffects __instance, float healthNum)
        {
            EcsNetManager.Instance?.OnLocalHealthChanged(__instance.agent, healthNum);
        }
    }

    [HarmonyPatch]
    internal static class EcsStatusAddHook_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(StatusEffects))
                .Where(m => m.Name == "AddStatusEffect")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(StatusEffects __instance, string statusEffectName)
        {
            EcsNetManager.Instance?.OnLocalStatusChanged(__instance.agent, statusEffectName, true);
        }
    }

    [HarmonyPatch]
    internal static class EcsStatusRemoveHook_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(StatusEffects))
                .Where(m => m.Name == "RemoveStatusEffect")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(StatusEffects __instance, string statusEffectName)
        {
            EcsNetManager.Instance?.OnLocalStatusChanged(__instance.agent, statusEffectName, false);
        }
    }

    [HarmonyPatch]
    internal static class EcsDeathHook_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(StatusEffects))
                .Where(m => m.Name == "SetupDeath")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(StatusEffects __instance)
        {
            EcsNetManager.Instance?.OnAgentDeath(__instance.agent);
        }
    }

    [HarmonyPatch(typeof(LoadLevel), "IncreaseLevel")]
    internal static class EcsLevelAdvanceHook_Patch
    {
        private static void Postfix()
        {
            EcsNetManager.Instance?.OnLocalLevelAdvance();
        }
    }

    // NPC spawn-order registry: every agent spawned during the load window
    // gets the next index; the same index means the same NPC on every
    // instance (spawn order is seed-deterministic).
    [HarmonyPatch]
    internal static class EcsNpcRegistryHook_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(SpawnerMain))
                .Where(m => m.Name == "SpawnAgent")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(Agent __result)
        {
            if (__result != null)
                EcsNetManager.Instance?.RegisterNpcSpawn(__result);
        }

        // EXPERIMENTAL (branch npc-dynamic-spawns): followers cancel
        // game-initiated post-load NPC spawns — the authority's copy arrives
        // as a dynamic npc entity instead. Callers receiving null is the risk
        // under test; BypassSuppression exempts our own deliberate spawns.
        private static bool Prefix(ref Agent __result, int playerColor, string agentType)
        {
            if (NpcSync.BypassSuppression || playerColor != 0)
                return true;
            // Pseudo-agents are local simulation helpers (fire's ObjectAgent,
            // etc.), never synced as NPCs — suppressing them null-crashes
            // their callers (Fire.SpawnObjectAgent derefs the result).
            // Mirror of the RegisterNpcSpawn exclusion.
            if (agentType == "ObjectAgent")
                return true;
            var manager = EcsNetManager.Instance;
            if (manager == null || !manager.ShouldSuppressDynamicSpawn)
                return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(LoadLevel), "SetupMore2")]
    internal static class EcsLevelGenHook_Patch
    {
        private static void Prefix()
        {
            EcsNetManager.Instance?.OnLevelGenerated();
        }
    }

    // Ground-item pickup by a local player. Success test: after Interact the
    // InvItem's database points at the picking agent's inventory. Remote
    // applies use DestroyMeFromClient (not Interact), so no loop.
    [HarmonyPatch(typeof(Item), "Interact", typeof(Agent))]
    internal static class EcsItemPickupHook_Patch
    {
        private static void Prefix(Item __instance, out UnityEngine.Vector2 __state)
        {
            __state = __instance.tr != null ? (UnityEngine.Vector2)__instance.tr.position : UnityEngine.Vector2.zero;
        }

        private static void Postfix(Item __instance, Agent agent, UnityEngine.Vector2 __state)
        {
            if (agent == null || agent.isPlayer <= 0 || agent.isPlayer == 99)
                return;
            if (__instance.invItem == null || __instance.invItem.database != agent.inventory)
                return;
            EcsNetManager.Instance?.OnLocalItemPickup(__instance.invItem.invItemName, __state);
        }
    }

    // Item dropped by a local player -> a ground item appeared. Remote
    // applies use SpawnerMain.SpawnItem (not DropItem), so no loop.
    [HarmonyPatch]
    internal static class EcsItemDropHook_Patch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(InvDatabase))
                .Where(m => m.Name == "DropItem")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

        private static void Postfix(InvDatabase __instance, Item __result)
        {
            var agent = __instance.agent;
            if (__result == null || agent == null || agent.isPlayer <= 0 || agent.isPlayer == 99)
                return;
            EcsNetManager.Instance?.OnLocalItemDrop(__result);
        }
    }

    // Publish doors opened by LOCAL players only. Remote applies go through
    // GameStateApi.OpenDoor with a null agent, so they don't loop back here.
    [HarmonyPatch(typeof(Door), "OpenDoor", typeof(Agent), typeof(bool))]
    internal static class EcsDoorHook_Patch
    {
        private static void Postfix(Door __instance, Agent myAgent)
        {
            if (myAgent != null && myAgent.isPlayer > 0 && myAgent.isPlayer != 99)
                EcsNetManager.Instance?.OnLocalDoorOpen(__instance);
        }
    }

    // Lock/Unlock take no agent (levers, keys, hacks all funnel here), so
    // remote applies are suppressed with a flag instead. Only publish when
    // the state actually changed (the non-authority branch routes through
    // ObjectMult without touching `locked`).
    [HarmonyPatch(typeof(Door), "Lock")]
    internal static class EcsDoorLockHook_Patch
    {
        private static void Postfix(Door __instance)
        {
            if (__instance.locked && !EcsNetManager.ApplyingRemoteDoor)
                EcsNetManager.Instance?.OnLocalDoorLock(__instance, true);
        }
    }

    [HarmonyPatch(typeof(Door), "Unlock")]
    internal static class EcsDoorUnlockHook_Patch
    {
        private static void Postfix(Door __instance)
        {
            if (!__instance.locked && !EcsNetManager.ApplyingRemoteDoor)
                EcsNetManager.Instance?.OnLocalDoorLock(__instance, false);
        }
    }

    // Publish object destruction from any local cause (player, NPC, fire,
    // chain explosions). DestroyMe only acts when `destroying` flips false ->
    // true, which also makes remote applies idempotent: duplicates and echos
    // hit an already-destroying object and no-op.
    [HarmonyPatch(typeof(ObjectReal), "DestroyMe", typeof(PlayfieldObject))]
    internal static class EcsObjectDestroyHook_Patch
    {
        private static void Prefix(ObjectReal __instance, ref bool __state) =>
            __state = __instance.destroying;

        private static void Postfix(ObjectReal __instance, bool __state)
        {
            if (!__state && __instance.destroying && !EcsNetManager.ApplyingRemoteObject)
                EcsNetManager.Instance?.OnLocalObjectDestroyed(__instance);
        }
    }

    // Publish weapon equips by LOCAL players (avatars equip through the same
    // method but aren't local players, so no echo loop).
    [HarmonyPatch(typeof(InvDatabase), "EquipWeapon", typeof(InvItem), typeof(bool))]
    internal static class EcsEquipHook_Patch
    {
        private static void Postfix(InvDatabase __instance, InvItem item)
        {
            if (__instance.agent != null && item != null)
                EcsNetManager.Instance?.OnLocalWeaponEquipped(__instance.agent, item.invItemName);
        }
    }

    // Publish new fires from the 8-arg SpawnFire master. A non-null result
    // means it actually spawned (the master dedups by position/burning
    // object and returns null otherwise). neverGoOut fires are skipped:
    // those come from generation emitters (flame grates, fire spewers) that
    // both instances simulate identically already.
    [HarmonyPatch(typeof(SpawnerMain), "SpawnFire",
        typeof(PlayfieldObject), typeof(GameObject), typeof(Vector3), typeof(bool), typeof(bool), typeof(int), typeof(bool), typeof(bool))]
    internal static class EcsFireSpawnHook_Patch
    {
        private static void Postfix(Fire __result, bool oilFire, bool neverGoOut)
        {
            if (__result != null && !neverGoOut && !EcsNetManager.ApplyingRemoteFire)
                EcsNetManager.Instance?.OnLocalFireSpawned(__result, oilFire);
        }
    }

    // Publish extinguish/burn-out from Fire.DestroyMe's destroying edge.
    // Level transitions mass-destroy fires — never publish those.
    [HarmonyPatch(typeof(Fire), "DestroyMe", new System.Type[0])]
    internal static class EcsFireOutHook_Patch
    {
        private static void Prefix(Fire __instance, ref bool __state) =>
            __state = __instance.destroying;

        private static void Postfix(Fire __instance, bool __state)
        {
            var gc = GameController.gameController;
            if (!__state && __instance.destroying && !EcsNetManager.ApplyingRemoteFire
                && gc != null && !gc.levelTransitioning)
                EcsNetManager.Instance?.OnLocalFireOut(__instance);
        }
    }
}
