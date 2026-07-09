using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace SorTestDriver
{
    // Headless end-to-end test harness. Inert unless SOR_TEST_MODE is set.
    //
    //   SOR_TEST_MODE   = host | client
    //   SOR_TEST_ADDR   = host address for clients      (default 127.0.0.1)
    //   SOR_TEST_PORT   = LAN port                      (default 7777)
    //   SOR_TEST_NAME   = multiplayer player name       (default TestN)
    //   SOR_TEST_REPORT = file to append status lines to
    //   SOR_TEST_CHAR   = agent name to pick on the character select screen
    //   SOR_TEST_CAST   = 1: press the special ability every 5s and report state
    //   SOR_TEST_ACCEPT_DELAY = seconds to leave the select screen open first
    [BepInPlugin("com.hypnodroid.sortestdriver", "SoR Test Driver", "0.1.0")]
    public class TestDriverPlugin : BaseUnityPlugin
    {
        internal static bool TestModeActive;

        private string mode, addr, port, playerName, reportPath, testChar;
        private bool castTest;
        private float acceptDelay;
        private float charSelectSeen = -1f;
        private string state = "boot";
        private float nextAttempt, nextReport, nextCast, started;
        private int exceptionCount;

        private void Awake()
        {
            mode = Environment.GetEnvironmentVariable("SOR_TEST_MODE");
            if (string.IsNullOrEmpty(mode))
            {
                enabled = false;
                return;
            }
            TestModeActive = true;
            addr = Environment.GetEnvironmentVariable("SOR_TEST_ADDR") ?? "127.0.0.1";
            port = Environment.GetEnvironmentVariable("SOR_TEST_PORT") ?? "7777";
            playerName = Environment.GetEnvironmentVariable("SOR_TEST_NAME") ?? "Test" + UnityEngine.Random.Range(100, 999);
            reportPath = Environment.GetEnvironmentVariable("SOR_TEST_REPORT");
            testChar = Environment.GetEnvironmentVariable("SOR_TEST_CHAR");
            castTest = Environment.GetEnvironmentVariable("SOR_TEST_CAST") == "1";
            float.TryParse(Environment.GetEnvironmentVariable("SOR_TEST_ACCEPT_DELAY") ?? "0", out acceptDelay);
            started = Time.realtimeSinceStartup;
            new Harmony("com.hypnodroid.sortestdriver").PatchAll(typeof(TestDriverPlugin).Assembly);
            Report($"driver-start mode={mode} name={playerName} addr={addr}:{port}");
        }

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                if (exceptionCount++ < 20)
                {
                    string top = (e.StackTrace ?? "").Split('\n')[0].Trim();
                    Report($"exception state={state}: {e.GetType().Name} {e.Message} @ {top}");
                }
            }
        }

        private void Tick()
        {
            float now = Time.realtimeSinceStartup;
            var gc = GameController.gameController;

            if (now >= nextReport)
            {
                nextReport = now + 5f;
                ReportStatus(gc);
            }

            if (gc == null || gc.menuGUI == null)
                return;

            switch (state)
            {
                case "boot":
                    if (now - started > 12f)
                        state = "open-lan";
                    break;

                case "open-lan":
                    if (now < nextAttempt)
                        return;
                    nextAttempt = now + 3f;
                    try { gc.menuGUI.OpenMultiplayerLAN(); }
                    catch (Exception e) { Report("OpenMultiplayerLAN threw: " + e.Message); }
                    if (FindField("LANPlayerNameField") != null)
                        state = "start";
                    break;

                case "start":
                    var nameField = FindField("LANPlayerNameField");
                    if (nameField == null)
                    {
                        state = "open-lan";
                        return;
                    }
                    nameField.text = playerName;
                    gc.menuGUI.levelSettingLAN = "NewGame";
                    if (mode == "host")
                    {
                        FindField("LANHostPortField").text = port;
                        gc.menuGUI.PressedStartLANHostButton();
                        Report("pressed-host");
                    }
                    else
                    {
                        FindField("LANClientPortField").text = port;
                        FindField("LANClientAddressField").text = addr;
                        gc.menuGUI.PressedStartLANClientButton();
                        Report("pressed-join");
                    }
                    state = "in-game";
                    nextAttempt = now + 5f;
                    break;

                case "in-game":
                    // Auto-accept the default character whenever the select screen opens
                    if (now >= nextAttempt && gc.mainGUI != null && gc.mainGUI.openedCharacterSelect)
                    {
                        var cs = gc.mainGUI.characterSelectScript;
                        if (cs != null && cs.choiceAccepted != null && cs.choiceAccepted.Length > 0 && !cs.choiceAccepted[0])
                        {
                            if (charSelectSeen < 0f)
                            {
                                charSelectSeen = now;
                                Report("char-select-open");
                            }
                            if (now < charSelectSeen + acceptDelay)
                                return;
                            nextAttempt = now + 3f;
                            if (!string.IsNullOrEmpty(testChar) && cs.curSelected != null && cs.curSelected.Length > 0)
                                cs.curSelected[0] = testChar;
                            cs.AcceptChoice(0);
                            Report("accepted-character" + (string.IsNullOrEmpty(testChar) ? "" : " forced=" + testChar));
                        }
                    }
                    AbilityTick(gc, now);
                    break;
            }
        }

        // Reports the player agent's identity/ability state and (SOR_TEST_CAST=1)
        // presses the special ability on a timer to smoke-test it headlessly.
        private void AbilityTick(GameController gc, float now)
        {
            if (!gc.loadComplete || gc.playerAgent == null || now < nextCast)
                return;
            nextCast = now + 5f;
            var a = gc.playerAgent;
            var item = a.inventory == null ? null : a.inventory.equippedSpecialAbility;
            Report($"agent={a.agentName} ability={a.specialAbility ?? "-"}"
                 + $" abilityCount={(item == null ? "none" : item.invItemCount.ToString())}"
                 + $" hp={a.health} pos=({a.tr.position.x:F1},{a.tr.position.y:F1})");
            if (castTest && !string.IsNullOrEmpty(a.specialAbility))
            {
                bool used = a.statusEffects.PressedSpecialAbility();
                Report($"pressed-ability used={used}");
            }
        }

        private static InputField FindField(string name)
        {
            var go = GameObject.Find(name);
            return go == null ? null : go.GetComponent<InputField>();
        }

        private void ReportStatus(GameController gc)
        {
            string s;
            if (gc == null)
            {
                s = "gc=null";
            }
            else
            {
                int numPlayers = -1, conns = -1;
                try { if (gc.networkManagerUWP != null) numPlayers = gc.networkManagerUWP.FindNumPlayers(); } catch { }
                try { conns = NetworkServer.active ? NetworkServer.connections.Count : -1; } catch { }
                s = $"state={state} level={gc.levelType} mult={gc.multiplayerMode} serverActive={NetworkServer.active} clientConn={NetworkClient.isConnected}"
                  + $" numPlayers={numPlayers} conns={conns} agents={(gc.playerAgentList == null ? -1 : gc.playerAgentList.Count)}"
                  + $" loadComplete={gc.loadComplete} charSelect={(gc.mainGUI != null && gc.mainGUI.openedCharacterSelect)}";
            }
            Report(s);
        }

        private void Report(string line)
        {
            string stamped = $"[{Time.realtimeSinceStartup - started,7:F1}s] {line}";
            Logger.LogInfo("TESTDRIVER " + stamped);
            if (reportPath == null)
                return;
            try { File.AppendAllText(reportPath, stamped + "\n"); } catch { }
        }
    }

    // Directly-launched test instances cannot reach the Steam client (flatpak PID
    // namespace), and the game then falls back to GOG Galaxy, whose native library
    // does not ship in the Steam build - producing a per-frame DllNotFoundException
    // storm that wedges level loading. In test mode force the game into plain
    // offline-platform mode; LAN multiplayer works without any platform.
    [HarmonyPatch(typeof(GameController), "Awake")]
    internal static class NoPlatform_Patch
    {
        private static void Postfix(GameController __instance)
        {
            if (!TestDriverPlugin.TestModeActive)
                return;
            __instance.usingGog = false;
            __instance.usingGalaxy = false;
            if (__instance.sessionDataBig != null)
                __instance.sessionDataBig.usingSteam = false;
        }
    }

    [HarmonyPatch(typeof(NetworkManagerUWP), "RealAwake")]
    internal static class NoSteamNetwork_Patch
    {
        private static void Postfix(NetworkManagerUWP __instance)
        {
            if (!TestDriverPlugin.TestModeActive)
                return;
            var gc = GameController.gameController;
            if (gc != null && gc.sessionDataBig != null)
                gc.sessionDataBig.usingSteam = false;
            // -batchmode makes Mirror auto-start a server on every instance,
            // which steals the LAN port from the real host. Never auto-start.
            __instance.headlessStartMode = HeadlessStartOptions.DoNothing;
        }
    }

    [HarmonyPatch(typeof(GalaxyManager), "RealEnable")]
    internal static class NoGalaxyInit_Patch
    {
        private static bool Prefix(ref System.Collections.IEnumerator __result)
        {
            if (!TestDriverPlugin.TestModeActive)
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
        private static bool Prefix() => !TestDriverPlugin.TestModeActive;
    }

    [HarmonyPatch(typeof(GalaxyManager), "OnDestroy")]
    internal static class NoGalaxyShutdown_Patch
    {
        private static bool Prefix() => !TestDriverPlugin.TestModeActive;
    }
}
