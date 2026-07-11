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
        internal static ConfigEntry<bool> EcsRealAvatars;
        internal static ConfigEntry<bool> EcsFollowLevel;
        internal static ConfigEntry<bool> EcsNpcSync;
        internal static ConfigEntry<bool> EcsSuppressDynamicSpawns;
        internal static ConfigEntry<bool> TraceEnabled;
        internal static ManualLogSource Log;
        internal static EightPlayersPlugin Instance;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
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
            EcsRealAvatars = Config.Bind("EcsNet", "RealAvatars", true,
                "Spawn a real (AI-disabled) game character for each remote player in the same world instead of a ghost marker.");
            EcsFollowLevel = Config.Bind("EcsNet", "FollowRoomLevel", true,
                "Automatically take the next-level transition when the room's party has moved ahead, so everyone travels together.");
            EcsNpcSync = Config.Bind("EcsNet", "NpcSync", true,
                "Mirror NPC positions from the room's NPC authority (lowest client id) so everyone sees the same characters in the same places.");
            EcsSuppressDynamicSpawns = Config.Bind("EcsNet", "SuppressDynamicSpawns", true,
                "EXPERIMENTAL: followers cancel game-initiated post-load NPC spawns and rely on the authority's dynamic-npc entities instead. Turn off if NPC-related errors appear.");

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
            if (!_behaviorEngineDown)
            {
                try
                {
                    TickBehaviorEngine();
                }
                catch (System.Exception e)
                {
                    // Most likely MoonSharp.Interpreter.dll missing from the
                    // plugins dir (the deploy must copy BOTH dlls). Disable
                    // code mode; everything else keeps working.
                    _behaviorEngineDown = true;
                    Log.LogError($"BehaviorEngine disabled: {e.GetType().Name}: {e.Message}");
                }
            }
            LoadWatchdog.Tick();
            Tracing.Trace.Tick();
        }

        private static bool _behaviorEngineDown;

        // Separate method so a MoonSharp assembly-load failure surfaces as a
        // catchable exception at THIS call site instead of failing the JIT of
        // Update itself.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void TickBehaviorEngine() => BehaviorEngine.Tick();

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
            var gc = GameController.gameController;
            if (gc == null)
                return;

            // Single-player level loads reuse sessionData.randomListTable when
            // it is non-empty (RandomSelection.LoadRandomness). A HomeBase ->
            // level-1 transition can leave a PARTIAL table (early lists only);
            // loadStuff2 then dies on randomListTable["SyringeContents"] and
            // the load wedges forever. Clear partial tables so the game
            // refills them from scratch.
            if (gc.sessionData != null && gc.sessionData.randomListTable != null
                && gc.sessionData.randomListTable.Count != 0
                && (!gc.sessionData.randomListTable.ContainsKey("SyringeContents")      // late fill (LoadRandomness)
                    || !gc.sessionData.randomListTable.ContainsKey("FloorTilesBuilding"))) // early fill (LoadRandomnessEarly)
            {
                EightPlayersPlugin.Log.LogWarning(
                    $"sessionData.randomListTable is partial ({gc.sessionData.randomListTable.Count} lists) - clearing so the game refills it");
                gc.sessionData.randomListTable.Clear();
                if (gc.sessionData.randomListTableStatic != null)
                    gc.sessionData.randomListTableStatic.Clear();
                // The refill only happens while setupRandomness is false —
                // on mid-session loads it is already true and the cleared
                // table stays EMPTY, killing loadStuff2 (KeyNotFound
                // 'SyringeContents') and wedging the level load forever.
                if (gc.randomSelection != null)
                    gc.randomSelection.setupRandomness = false;
            }

            var seed = System.Environment.GetEnvironmentVariable("SOR_SEED")
                       ?? EcsNet.EcsNetManager.AdoptedSeed;
            if (string.IsNullOrEmpty(seed) || gc.sessionDataBig == null)
                return;
            // Base seed only — a stale qualified seed ("abc#2") can arrive
            // here via a save-polluted claim; never compound qualification.
            seed = seed.Split('#')[0];
            _forcedThisLoad = true;
            // Generation must be a pure function of (room seed, level),
            // independent of the LOAD HISTORY that led here. Vanilla breaks
            // that two ways: loadStuff does randomSeedNum++ per replayed load
            // (elevator path), and sessionData.usedChunks accumulates across
            // every load in the session with generation EXCLUDING used chunks
            // — so menu/demo worlds, adoption reloads and divergence-heal
            // reloads all shift later maps even at identical seeds. Normalize
            // all three inputs on every forced load: level-qualified seed
            // string, numeric seed re-derived from it, empty chunk history.
            // (Trade-off: chunk variety no longer carries across levels; every
            // instance in the room makes the same trade, which is the point.)
            // Level 1 (and the level-0 menu load) keep the RAW seed: the
            // room CLAIMER's boot world is generated from vanilla's
            // random-letter path (numeric = GetHashCode(letters)) and — in
            // host mode — is never reloaded, so followers must derive the
            // identical numeric seed. Levels 2+ get the level-qualified
            // string so replayed loads (heals, follows) always re-derive the
            // same per-level seed. (Uniform "#1" qualification broke host
            // mode: follower hash(seed#1) != claimer hash(seed).)
            int lvl = gc.sessionDataBig.curLevel;
            var qualified = lvl > 1 ? seed + "#" + lvl : seed;
            gc.sessionDataBig.userSetSeed = qualified;
            __instance.randomSeedNum = 0;   // always re-derive from the string
            if (gc.sessionData != null)
            {
                if (gc.sessionData.usedChunks != null)
                    gc.sessionData.usedChunks.Clear();
                // loadStuff calls sessionData.RetrieveSeeds() AFTER this
                // prefix when gotData is set (observed: the room claimer's
                // menu session stores seeds, the follower's doesn't) — it
                // would restore the stale seeds over our zeroing and skip
                // the userSetSeed derivation entirely. Zero the stored
                // copies so the restore is a no-op.
                gc.sessionData.randomSeedNum = 0;
                gc.sessionData.randomSeedLetter = "";
            }
            EightPlayersPlugin.Log.LogInfo($"Forcing map seed '{seed}' at level load (level {lvl}, qualified '{qualified}')");
        }

        private static bool _forcedThisLoad;

        // The game PERSISTS sessionDataBig.userSetSeed in the save; a forced
        // qualified seed leaking into it becomes the NEXT run's menu world
        // and gets claimed as the room seed (observed live: run N+1 claimed
        // run N's 'kcvkxwew#1' and worlds diverged 25/17). Clear it once
        // generation has consumed it — but only when WE forced this load.
        private static void Postfix()
        {
            if (!_forcedThisLoad)
                return;
            _forcedThisLoad = false;
            var gc = GameController.gameController;
            if (gc != null && gc.sessionDataBig != null)
                gc.sessionDataBig.userSetSeed = "";
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

    // A StatusEffectDisplay HUD widget can survive a level teardown and get
    // awakened (GameController.AwakenObjects) before the new level's player
    // is bound to its NonClickableGUI. Its RealStartB then NREs INSIDE the
    // WaitForRealStart load coroutine, killing it — the level generates
    // empty (agents=1, objects=0) and the game wedges forever. Observed
    // repeatedly in solo level transitions with the ECS layer active. A
    // stale HUD widget must not kill level loading: log and swallow; the
    // widget re-binds on the next RealStart pass.
    [HarmonyPatch(typeof(StatusEffectDisplay), "RealStartB")]
    internal static class StatusDisplayLoadGuard_Patch
    {
        private static System.Exception Finalizer(System.Exception __exception, StatusEffectDisplay __instance)
        {
            if (__exception != null)
                EightPlayersPlugin.Log.LogWarning(
                    $"StatusEffectDisplay.RealStartB threw {__exception.GetType().Name} on '{__instance?.name}' "
                    + $"(parent '{__instance?.transform?.parent?.name}') - suppressed so the level load survives");
            return null;
        }
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
