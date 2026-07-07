using System.Linq;
using System.Reflection;
using HarmonyLib;

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

        private static void Postfix(StatusEffects __instance)
        {
            EcsNetManager.Instance?.OnLocalHealthChanged(__instance.agent);
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
}
