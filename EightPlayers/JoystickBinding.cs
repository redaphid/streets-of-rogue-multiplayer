using System;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace EightPlayers
{
    // Multiple game windows on one computer all receive input from every connected
    // gamepad (the game hardwires Joysticks[0] to player 1, so pad #1 would drive
    // ALL windows and the other pads would sit idle).
    //
    // Setting the environment variable SOR_PAD=N before launching a window makes
    // that window listen to gamepad N only (1-based, in OS connection order):
    //   window 1: SOR_PAD=1   window 2: SOR_PAD=2   ...
    // Unset = vanilla behavior.
    //
    // Enforced right after the game's own controller assignment calls, plus
    // periodically to survive hot-plugs and Rewired auto-assignment.
    internal static class JoystickBinding
    {
        internal static int PadNumber;   // 0 = disabled
        private static float nextEnforce;

        internal static void Init()
        {
            var raw = Environment.GetEnvironmentVariable("SOR_PAD");
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var n) && n > 0)
            {
                PadNumber = n;
                EightPlayersPlugin.Log.LogInfo($"SOR_PAD={n}: this window will use gamepad #{n} only");
            }
        }

        internal static void Tick()
        {
            if (PadNumber <= 0 || Time.unscaledTime < nextEnforce)
                return;
            nextEnforce = Time.unscaledTime + 3f;
            Enforce();
        }

        internal static void Enforce()
        {
            if (PadNumber <= 0)
                return;
            try
            {
                if (!ReInput.isReady)
                    return;
                var joysticks = ReInput.controllers.Joysticks;
                Joystick target = PadNumber - 1 < joysticks.Count ? joysticks[PadNumber - 1] : null;

                foreach (var player in ReInput.players.AllPlayers)
                {
                    // snapshot: RemoveController mutates the live list
                    var assigned = new System.Collections.Generic.List<Joystick>(player.controllers.Joysticks);
                    foreach (var joystick in assigned)
                    {
                        if (joystick != target)
                            player.controllers.RemoveController(joystick);
                    }
                }

                if (target == null)
                    return;
                var pc = GameController.gameController != null ? GameController.gameController.playerControl : null;
                var player0 = pc != null && pc.rewiredPlayer != null && pc.rewiredPlayer.Length > 0
                    ? pc.rewiredPlayer[0]
                    : ReInput.players.GetPlayer(0);
                if (player0 != null && !player0.controllers.ContainsController(target))
                    player0.controllers.AddController(target, removeFromOtherPlayers: true);
            }
            catch (Exception e)
            {
                EightPlayersPlugin.Log.LogWarning("Joystick binding enforce failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(PlayerControl), "SetTitleControllers")]
    internal static class JoystickBindingTitle_Patch
    {
        private static void Postfix() => JoystickBinding.Enforce();
    }

    [HarmonyPatch(typeof(PlayerControl), "SetInGameControllers")]
    internal static class JoystickBindingInGame_Patch
    {
        private static void Postfix() => JoystickBinding.Enforce();
    }
}
