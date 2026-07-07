using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Rewired;
using UnityEngine;

namespace EightPlayers
{
    // Live debugging channel: drop commands into BepInEx/ep_cmd.txt while the game
    // runs; the plugin executes them and appends results to BepInEx/ep_out.txt
    // (also mirrored to LogOutput.log). Lets bindings be inspected and changed
    // without restarting the game.
    //
    // Commands (one per line):
    //   dump                          controllers, players, maps, values
    //   action <name>                 show current value + all bindings of an action
    //   bind <action> <+|-> <element name>       button-style binding
    //   bindaxis <action> <element name>         full-axis binding
    //   unbind <action>               remove all bindings of the action (p0 joystick maps)
    //   remap                         re-run the Zero 2 auto-layout from scratch
    //   nintendo <on|off>             flip printed-label mode and remap
    //   enable <category> <on|off>    enable/disable a joystick map category for p0
    internal static class CommandChannel
    {
        private static float next;
        private static string CmdPath => Path.Combine(BepInEx.Paths.BepInExRootPath, "ep_cmd.txt");
        private static string OutPath => Path.Combine(BepInEx.Paths.BepInExRootPath, "ep_out.txt");

        internal static void Tick()
        {
            if (Time.unscaledTime < next)
                return;
            next = Time.unscaledTime + 0.5f;
            string[] lines;
            try
            {
                if (!File.Exists(CmdPath))
                    return;
                lines = File.ReadAllLines(CmdPath);
                File.Delete(CmdPath);
            }
            catch
            {
                return;
            }
            foreach (var line in lines)
            {
                var cmd = line.Trim();
                if (cmd.Length == 0 || cmd.StartsWith("#"))
                    continue;
                try
                {
                    Out($"> {cmd}");
                    Execute(cmd);
                }
                catch (Exception e)
                {
                    Out($"error: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        private static void Execute(string cmd)
        {
            var parts = cmd.Split(new[] { ' ' }, 4);
            switch (parts[0].ToLowerInvariant())
            {
                case "dump": Dump(); break;
                case "action": ShowAction(parts[1]); break;
                case "bind": Bind(parts[1], parts[2] == "-" ? Pole.Negative : Pole.Positive, parts[3], ControllerElementType.Button, AxisRange.Full); break;
                case "bindaxis": Bind(parts[1], Pole.Positive, parts[2] + (parts.Length > 3 ? " " + parts[3] : ""), ControllerElementType.Axis, AxisRange.Full); break;
                case "unbind": Unbind(parts[1]); break;
                case "remap": ZeroTwoMapping.ForceRemap(); Out("remap requested"); break;
                case "nintendo":
                    EightPlayersPlugin.ZeroTwoNintendoLabels.Value = parts[1] == "on";
                    ZeroTwoMapping.ForceRemap();
                    Out($"nintendo labels {parts[1]}, remapping");
                    break;
                case "enable": Enable(parts[1], parts[2] == "on"); break;
                default: Out("unknown command"); break;
            }
        }

        private static Player P0 => ReInput.players.GetPlayer(0);

        private static IEnumerable<ControllerMap> P0JoystickMaps()
        {
            foreach (var joystick in P0.controllers.Joysticks)
                foreach (var map in P0.controllers.maps.GetMaps(ControllerType.Joystick, joystick.id))
                    yield return map;
        }

        private static Joystick FirstJoystick()
        {
            foreach (var j in P0.controllers.Joysticks)
                return j;
            return null;
        }

        private static void Dump()
        {
            var gc = GameController.gameController;
            Out($"p1Controller={gc?.sessionDataBig?.player1Controller} agentType={gc?.playerAgent?.controllerType} levelType={gc?.levelType}");
            foreach (var joystick in ReInput.controllers.Joysticks)
                Out($"joystick id={joystick.id} '{joystick.name}' hw='{joystick.hardwareName}'");
            foreach (var player in ReInput.players.AllPlayers)
            {
                if (player.controllers.joystickCount == 0)
                    continue;
                Out($"player {player.id} '{player.name}': {player.controllers.joystickCount} joystick(s)");
                foreach (var joystick in player.controllers.Joysticks)
                {
                    foreach (var map in player.controllers.maps.GetMaps(ControllerType.Joystick, joystick.id))
                    {
                        var cat = ReInput.mapping.GetMapCategory(map.categoryId);
                        Out($"  map cat='{cat?.name}' enabled={map.enabled} bindings={map.elementMapCount}");
                        foreach (var em in map.ElementMaps)
                        {
                            var action = ReInput.mapping.GetAction(em.actionId);
                            var el = joystick.GetElementIdentifierById(em.elementIdentifierId);
                            Out($"    {action?.name,-18} <- el{em.elementIdentifierId,-3} '{el?.name}' {em.elementType} {em.axisContribution} range={em.axisRange}");
                        }
                    }
                }
            }
            Out($"values: MoveXJ={P0.GetAxis("MoveXJ"):F2} MoveYJ={P0.GetAxis("MoveYJ"):F2} MoveXJraw={P0.GetAxisRaw("MoveXJ"):F2}");
        }

        private static void ShowAction(string name)
        {
            var action = ReInput.mapping.GetAction(name);
            if (action == null)
            {
                Out($"no action named '{name}'");
                return;
            }
            Out($"action '{name}' id={action.id} type={action.type} axis={P0.GetAxis(name):F2} raw={P0.GetAxisRaw(name):F2} button={P0.GetButton(name)}");
            foreach (var map in P0JoystickMaps())
            {
                foreach (var em in map.ElementMaps)
                {
                    if (em.actionId != action.id)
                        continue;
                    var cat = ReInput.mapping.GetMapCategory(map.categoryId);
                    var joystick = FirstJoystick();
                    var el = joystick?.GetElementIdentifierById(em.elementIdentifierId);
                    Out($"  [{cat?.name} enabled={map.enabled}] el{em.elementIdentifierId} '{el?.name}' {em.elementType} {em.axisContribution} range={em.axisRange} invert={em.invert}");
                }
            }
        }

        private static void Bind(string actionName, Pole pole, string elementName, ControllerElementType type, AxisRange range)
        {
            var action = ReInput.mapping.GetAction(actionName);
            var joystick = FirstJoystick();
            if (action == null || joystick == null)
            {
                Out($"missing action or joystick ({actionName})");
                return;
            }
            ControllerElementIdentifier element = null;
            foreach (var el in joystick.ElementIdentifiers)
                if (string.Equals(el.name, elementName, StringComparison.OrdinalIgnoreCase))
                    element = el;
            if (element == null)
            {
                Out($"no element named '{elementName}'");
                return;
            }
            foreach (var map in P0JoystickMaps())
            {
                var cat = ReInput.mapping.GetMapCategory(map.categoryId);
                if (cat?.name != "Gamepad")
                    continue;
                foreach (var em in new List<ActionElementMap>(map.ElementMaps))
                    if (em.actionId == action.id && em.axisContribution == pole && em.elementIdentifierId == element.id)
                        map.DeleteElementMap(em.id);
                bool ok = map.CreateElementMap(action.id, pole, element.id, type, range, invert: false);
                Out($"bind {actionName} {pole} <- '{element.name}' ({type}) => {(ok ? "ok" : "FAILED")}");
            }
        }

        private static void Unbind(string actionName)
        {
            var action = ReInput.mapping.GetAction(actionName);
            if (action == null)
            {
                Out($"no action named '{actionName}'");
                return;
            }
            int n = 0;
            foreach (var map in P0JoystickMaps())
                if (map.DeleteElementMapsWithAction(action.id))
                    n++;
            Out($"unbound '{actionName}' from {n} map(s)");
        }

        private static void Enable(string category, bool state)
        {
            int n = P0.controllers.maps.SetMapsEnabled(state, ControllerType.Joystick, category);
            Out($"category '{category}' -> {(state ? "on" : "off")} ({n} map(s))");
        }

        private static void Out(string line)
        {
            EightPlayersPlugin.Log.LogInfo("EPCMD " + line);
            try { File.AppendAllText(OutPath, line + "\n"); } catch { }
        }
    }
}
