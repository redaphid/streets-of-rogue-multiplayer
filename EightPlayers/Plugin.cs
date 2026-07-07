using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EightPlayers
{
    [BepInPlugin(Guid, "Eight Players", "0.1.0")]
    public class EightPlayersPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.hypnodroid.eightplayers";

        internal static ConfigEntry<int> MaxPlayers;
        internal static ConfigEntry<bool> ShowLanMenu;
        internal static ConfigEntry<bool> ZeroTwoAutoMap;
        internal static ConfigEntry<string> ZeroTwoMatch;
        internal static ConfigEntry<bool> DebugControllerLog;
        internal static ConfigEntry<bool> ZeroTwoNintendoLabels;
        internal static ConfigEntry<string> EcsServerUrl;
        internal static ConfigEntry<string> EcsRoom;
        internal static ConfigEntry<string> EcsPlayerName;
        internal static ConfigEntry<int> EcsSendHz;
        internal static ConfigEntry<bool> EcsShowHud;
        internal static ConfigEntry<bool> TraceEnabled;
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            MaxPlayers = Config.Bind("General", "MaxPlayers", 8,
                new ConfigDescription("Maximum total players in an online game (vanilla: 4)", new AcceptableValueRange<int>(2, 16)));
            ShowLanMenu = Config.Bind("General", "ShowLanMenu", true,
                "Re-enable the hidden LAN (direct IP) multiplayer menu. Needed to run several game instances on one computer.");
            ZeroTwoAutoMap = Config.Bind("Controllers", "ZeroTwoAutoMap", true,
                "Automatically install a compact button layout for 8BitDo Zero 2 controllers (no sticks/triggers).");
            ZeroTwoMatch = Config.Bind("Controllers", "ZeroTwoMatch", "8bitdo zero",
                "Space-separated tokens that must all appear in a joystick's name for it to be treated as a Zero 2.");
            DebugControllerLog = Config.Bind("Controllers", "DebugControllerLog", true,
                "Log a CTRLDBG line to LogOutput.log whenever controller state changes (for troubleshooting).");
            ZeroTwoNintendoLabels = Config.Bind("Controllers", "ZeroTwoNintendoLabels", true,
                "Bind Zero 2 face buttons by their PRINTED (Nintendo-layout) labels instead of reported Xbox positions.");

            EcsServerUrl = Config.Bind("EcsNet", "ServerUrl", "ws://localhost:8787",
                "sor-ecs-net worker base URL (ws:// for wrangler dev, wss://<worker>.workers.dev when deployed). Env override: SOR_ECS_SERVER.");
            EcsRoom = Config.Bind("EcsNet", "Room", "",
                "Room code to join (letters/digits/dashes). Empty disables the ECS network layer. Env override: SOR_ECS_ROOM.");
            EcsPlayerName = Config.Bind("EcsNet", "PlayerName", System.Environment.UserName,
                "Name shown above your ghost on other players' screens. Env override: SOR_ECS_NAME.");
            EcsSendHz = Config.Bind("EcsNet", "SendHz", 15,
                new ConfigDescription("Position updates per second sent to the room", new AcceptableValueRange<int>(1, 30)));
            EcsShowHud = Config.Bind("EcsNet", "ShowHud", true,
                "Show a one-line ECSNET connection status overlay in the top-left corner.");

            TraceEnabled = Config.Bind("Tracing", "Enabled", false,
                "Write a JSONL behavior trace (traces/trace-*.jsonl in the game dir) of state-mutating game events, used to verify ECS ports keep vanilla behavior. Env override: SOR_TRACE=1/0.");

            Tracing.Trace.Init();
            var harmony = new Harmony(Guid);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Tracing.NetTrace.Install(harmony);
            JoystickBinding.Init();
            gameObject.AddComponent<EcsNet.EcsNetManager>();
            Log.LogInfo($"EightPlayers loaded. Player cap: {MaxPlayers.Value}, LAN menu: {ShowLanMenu.Value}");
        }

        private void Update()
        {
            JoystickBinding.Tick();
            ZeroTwoMapping.Tick();
            ControllerDebug.Tick();
            CommandChannel.Tick();
            Tracing.Trace.Tick();
        }

        private void OnApplicationQuit()
        {
            Tracing.Trace.Shutdown();
        }
    }

    // Vanilla initializes 4 network player slots (connection list, colors, character
    // choices, ping timers...). Pad every per-slot list up to MaxPlayers and raise
    // Mirror's connection cap.
    [HarmonyPatch(typeof(NetworkManagerUWP), "RealAwake")]
    internal static class NetworkSlots_Patch
    {
        private static void Postfix(NetworkManagerUWP __instance)
        {
            var nmB = __instance.GetComponent<NetworkManagerB>();
            if (nmB == null)
                return;

            int max = EightPlayersPlugin.MaxPlayers.Value;
            while (nmB.colorsAvailable.Count < max) nmB.colorsAvailable.Add(true);
            while (nmB.networkConnectionList.Count < max) nmB.networkConnectionList.Add(null);
            while (nmB.agentListSolid.Count < max) nmB.agentListSolid.Add(null);
            while (nmB.verifiedList.Count < max) nmB.verifiedList.Add(false);
            while (nmB.playerControllerIDList.Count < max) nmB.playerControllerIDList.Add(0u);
            while (nmB.timeSinceLastPing.Count < max) nmB.timeSinceLastPing.Add(-1f);
            while (nmB.nextCharacter.Count < max) nmB.nextCharacter.Add("");
            while (nmB.currentCharacter.Count < max) nmB.currentCharacter.Add("");
            while (nmB.nextCharacterSuperSpecial.Count < max) nmB.nextCharacterSuperSpecial.Add(false);

            // Mirror-level connection cap (host itself does not consume a slot; leave headroom)
            __instance.maxConnections = max + 2;

            EightPlayersPlugin.Log.LogInfo($"Network player slots extended to {max} (maxConnections={__instance.maxConnections})");
        }
    }

    // WaitUntilLoadComplete is the server-side coroutine that admits a joining player.
    // It contains the two hardcoded caps:
    //     if (currentNum < 4) currentNum++;
    //     if (FindNumPlayers() > 4) serverInitiallyFull = true;   -> client gets "ServerFull"
    // Rewrite both 4s to MaxPlayers. Only constants directly preceded by the
    // currentNum field load or the FindNumPlayers() call are touched.
    [HarmonyPatch]
    internal static class ServerFull_Patch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.EnumeratorMoveNext(
                AccessTools.Method(typeof(NetworkManagerUWP), "WaitUntilLoadComplete"));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var findNumPlayers = AccessTools.Method(typeof(NetworkManagerUWP), nameof(NetworkManagerUWP.FindNumPlayers));
            var currentNum = AccessTools.Field(typeof(NetworkManagerUWP), nameof(NetworkManagerUWP.currentNum));
            int max = EightPlayersPlugin.MaxPlayers.Value;

            var list = new List<CodeInstruction>(instructions);
            int patched = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (!list[i].LoadsConstant(4))
                    continue;
                var prev = list[i - 1];
                bool afterFindNumPlayers = (prev.opcode == OpCodes.Call || prev.opcode == OpCodes.Callvirt)
                    && Equals(prev.operand, findNumPlayers);
                bool afterCurrentNum = prev.LoadsField(currentNum);
                if (afterFindNumPlayers || afterCurrentNum)
                {
                    list[i].opcode = OpCodes.Ldc_I4;
                    list[i].operand = max;
                    patched++;
                }
            }

            EightPlayersPlugin.Log.LogInfo($"ServerFull patch: rewrote {patched} player-cap constant(s) (expected 2)");
            if (patched == 0)
                EightPlayersPlugin.Log.LogError("ServerFull patch found nothing to rewrite - game update may have changed the code!");
            return list;
        }
    }

    // Host screen: the +/- player-limit button is clamped to 4. Reimplement with our cap.
    [HarmonyPatch(typeof(MenuGUI), "PressedIncreasePlayerLimit")]
    internal static class PlayerLimitButton_Patch
    {
        private static bool Prefix(MenuGUI __instance)
        {
            var gc = GameController.gameController;
            gc.audioHandler.PlayMust(gc.playerAgent, "MenuMove");
            if (__instance.playerLimitSetting < EightPlayersPlugin.MaxPlayers.Value)
                __instance.playerLimitSetting++;
            __instance.SetPlayerLimitText();
            return false;
        }
    }

    // SOR_SEED=<string> forces a deterministic map seed (the game's own
    // user-set-seed path in loadStuff2). Used by the trace-parity harness:
    // two runs with the same SOR_SEED must generate identical worlds.
    // Applied at generation kickoff because QuitToMainMenuClearStuff wipes
    // userSetSeed during the boot-to-menu flow.
    [HarmonyPatch(typeof(LoadLevel), "loadStuff")]
    internal static class ForceSeed_Patch
    {
        private static void Prefix(LoadLevel __instance)
        {
            var seed = System.Environment.GetEnvironmentVariable("SOR_SEED")
                       ?? EcsNet.EcsNetManager.AdoptedSeed;
            if (string.IsNullOrEmpty(seed))
                return;
            var gc = GameController.gameController;
            if (gc == null || gc.sessionDataBig == null)
                return;
            gc.sessionDataBig.userSetSeed = seed;
            EightPlayersPlugin.Log.LogInfo($"Forcing map seed '{seed}' at level load (env or room world)");
        }
    }

    // When the game starts without a reachable Steam client (e.g. a second window
    // launched directly for split-screen-style play), vanilla falls back to GOG
    // Galaxy. The Steam build ships no Galaxy native library, so every frame throws
    // DllNotFoundException and multiplayer wedges. If Steam did not initialize,
    // drop to plain platform-less mode instead - LAN play works fine there.
    [HarmonyPatch(typeof(GameController), "Awake")]
    internal static class NoSteamFallback_Patch
    {
        internal static bool SteamUp()
        {
            try { return SteamManager.Initialized; }
            catch { return false; }
        }

        private static void Postfix(GameController __instance)
        {
            if (SteamUp())
                return;
            __instance.usingGog = false;
            __instance.usingGalaxy = false;
            if (__instance.sessionDataBig != null)
                __instance.sessionDataBig.usingSteam = false;
            EightPlayersPlugin.Log.LogInfo("Steam not initialized - running platform-less (LAN multiplayer only)");
        }
    }

    [HarmonyPatch(typeof(NetworkManagerUWP), "RealAwake")]
    internal static class NoSteamFallbackNetwork_Patch
    {
        private static void Postfix()
        {
            var gc = GameController.gameController;
            if (!NoSteamFallback_Patch.SteamUp() && gc != null && gc.sessionDataBig != null)
                gc.sessionDataBig.usingSteam = false;
        }
    }

    [HarmonyPatch(typeof(GalaxyManager), "RealEnable")]
    internal static class NoGalaxyInit_Patch
    {
        private static bool Prefix(ref System.Collections.IEnumerator __result)
        {
            if (NoSteamFallback_Patch.SteamUp())
                return true;
            __result = Empty();
            return false;
        }

        private static System.Collections.IEnumerator Empty()
        {
            yield break;
        }
    }

    [HarmonyPatch(typeof(GalaxyManager), "Update")]
    internal static class NoGalaxyUpdate_Patch
    {
        private static bool Prefix() => NoSteamFallback_Patch.SteamUp();
    }

    [HarmonyPatch(typeof(GalaxyManager), "OnDestroy")]
    internal static class NoGalaxyShutdown_Patch
    {
        private static bool Prefix() => NoSteamFallback_Patch.SteamUp();
    }

    // The multiplayer menu contains a fully functional LAN (direct IP host/join) page
    // that the developers hide at menu setup, shuffling the remaining buttons up to
    // close the gap. Un-hide it and restore the original button positions.
    [HarmonyPatch(typeof(MenuGUI), "RealAwake")]
    internal static class LanMenu_Patch
    {
        private static void Postfix(MenuGUI __instance)
        {
            if (!EightPlayersPlugin.ShowLanMenu.Value || __instance.multiplayerMenuContent == null)
                return;
            var content = __instance.multiplayerMenuContent.transform;
            var lanButton = content.Find("MultiplayerLANButton");
            if (lanButton == null || lanButton.gameObject.activeSelf)
                return;

            lanButton.gameObject.SetActive(true);
            Shift(content, "MultiplayerHostButton", 80f);
            Shift(content, "MultiplayerJoinButton", 80f);
            Shift(content, "GoBackButtonMultiplayerMenu", -80f);
            EightPlayersPlugin.Log.LogInfo("LAN multiplayer menu re-enabled");
        }

        private static void Shift(Transform content, string name, float dy)
        {
            var t = content.Find(name);
            if (t != null)
                t.localPosition = new Vector2(t.localPosition.x, t.localPosition.y + dy);
        }
    }
}
