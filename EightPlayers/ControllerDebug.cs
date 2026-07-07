using System.Text;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace EightPlayers
{
    // This build of the game hides the Controller Type settings button, so
    // sessionDataBig.player1Controller stays "Keyboard" forever and the game
    // disables the joystick "Gamepad" map (movement reads MoveXJ only when
    // agent.controllerType != "Keyboard"). A window launched with SOR_PAD=N is
    // by definition a gamepad window: force the setting before the game's
    // controller setup derives everything from it.
    [HarmonyPatch(typeof(PlayerControl), "SetInGameControllers")]
    internal static class GamepadWindowInGame_Patch
    {
        private static void Prefix() => GamepadWindow.ForceGamepadPlayer1();
    }

    [HarmonyPatch(typeof(PlayerControl), "SetTitleControllers")]
    internal static class GamepadWindowTitle_Patch
    {
        private static void Prefix() => GamepadWindow.ForceGamepadPlayer1();
    }

    internal static class GamepadWindow
    {
        internal static void ForceGamepadPlayer1()
        {
            if (JoystickBinding.PadNumber <= 0)
                return;
            var gc = GameController.gameController;
            if (gc == null || gc.sessionDataBig == null)
                return;
            if (gc.sessionDataBig.player1Controller != "Gamepad")
            {
                gc.sessionDataBig.player1Controller = "Gamepad";
                EightPlayersPlugin.Log.LogInfo("SOR_PAD set: forcing player 1 controller type to Gamepad");
            }
        }
    }

    // Once-per-second state line, only when something changed. Lets us watch what
    // the game sees from the controller: CTRLDBG p1=<setting> agent=<type>
    // joy=<count> maps=<enabled> move=<x>,<y> btns=<pressed element names>
    internal static class ControllerDebug
    {
        private static float next;
        private static string last = "";

        internal static void Tick()
        {
            if (!EightPlayersPlugin.DebugControllerLog.Value || Time.unscaledTime < next)
                return;
            next = Time.unscaledTime + 1f;
            try
            {
                var sb = new StringBuilder("CTRLDBG ");
                var gc = GameController.gameController;
                sb.Append("p1=").Append(gc != null && gc.sessionDataBig != null ? gc.sessionDataBig.player1Controller : "?");
                sb.Append(" agent=").Append(gc != null && gc.playerAgent != null ? gc.playerAgent.controllerType : "-");
                if (!ReInput.isReady)
                {
                    sb.Append(" rewired=not-ready");
                    Emit(sb.ToString());
                    return;
                }
                sb.Append(" joy=").Append(ReInput.controllers.joystickCount);
                var p0 = ReInput.players.GetPlayer(0);
                if (p0 != null)
                {
                    sb.Append(" p0joys=").Append(p0.controllers.joystickCount);
                    foreach (var joystick in p0.controllers.Joysticks)
                    {
                        foreach (var map in p0.controllers.maps.GetMaps(ControllerType.Joystick, joystick.id))
                        {
                            var cat = ReInput.mapping.GetMapCategory(map.categoryId);
                            sb.Append(' ').Append(cat != null ? cat.name : map.categoryId.ToString())
                              .Append(map.enabled ? "=on" : "=off");
                        }
                    }
                    sb.Append(" move=").Append(p0.GetAxis("MoveXJ").ToString("F1"))
                      .Append(',').Append(p0.GetAxis("MoveYJ").ToString("F1"))
                      .Append(" raw=").Append(p0.GetAxisRaw("MoveXJ").ToString("F1"));
                    var moveX = ReInput.mapping.GetAction("MoveXJ");
                    var menuUp = ReInput.mapping.GetAction("MenuUpJ");
                    foreach (var joystick in p0.controllers.Joysticks)
                    {
                        foreach (var map in p0.controllers.maps.GetMaps(ControllerType.Joystick, joystick.id))
                        {
                            foreach (var em in map.ElementMaps)
                            {
                                if (moveX != null && em.actionId == moveX.id)
                                    sb.Append(" X<-el").Append(em.elementIdentifierId).Append('/').Append(em.axisContribution.ToString()[0]).Append('/').Append(em.elementType.ToString()[0]);
                                else if (menuUp != null && em.actionId == menuUp.id)
                                    sb.Append(" MU<-el").Append(em.elementIdentifierId);
                            }
                        }
                    }
                    sb.Append(" menu=").Append(p0.GetButton("MenuUpJ") ? "U" : "")
                      .Append(p0.GetButton("MenuDownJ") ? "D" : "")
                      .Append(p0.GetButton("MenuLeftJ") ? "L" : "")
                      .Append(p0.GetButton("MenuRightJ") ? "R" : "");
                }
                foreach (var joystick in ReInput.controllers.Joysticks)
                {
                    foreach (var element in joystick.ElementIdentifiers)
                    {
                        if (element.id <= 21 && joystick.GetButtonById(element.id))
                            sb.Append(" [").Append(element.name).Append(']');
                    }
                }
                Emit(sb.ToString());
            }
            catch (System.Exception e)
            {
                Emit("CTRLDBG error: " + e.Message);
            }
        }

        private static void Emit(string line)
        {
            if (line == last)
                return;
            last = line;
            EightPlayersPlugin.Log.LogInfo(line);
        }
    }
}
