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
