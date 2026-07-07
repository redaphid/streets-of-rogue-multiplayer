using System;
using System.Collections.Generic;
using HarmonyLib;
using Rewired;

namespace EightPlayers
{
    // The 8BitDo Zero 2 has no analog sticks and no triggers, so the game's default
    // gamepad layout leaves it unable to move or attack. When a joystick matching
    // its name is assigned to a player, install a compact layout:
    //
    //   D-pad  move (and menu navigation)      X  attack        A  interact
    //   B      cancel                          Y  special ability
    //   L      inventory                       R  cycle item
    //   Select use health item                 Start  pause menu
    //
    // Element identifiers differ by controller mode/OS, so elements are looked up
    // by name at runtime and everything found/missing is logged. Turn on Auto-Aim
    // in gameplay settings to compensate for the missing right stick.
    internal static class ZeroTwoMapping
    {
        private static readonly HashSet<int> mappedJoystickIds = new HashSet<int>();
        private static float nextScan;

        // action name -> candidate element names (first found wins).
        // Axis actions bind each direction as a button-to-axis contribution.
        private static (string action, Pole pole, string[] elements)[] GetLayout()
        {
            // The Zero 2's printed labels are Nintendo layout (A right, B bottom,
            // X top, Y left) while in XInput mode it reports Xbox POSITIONS.
            // Bind by printed label so "press A" means the button that says A:
            //   printed A = reported B, printed B = reported A,
            //   printed X = reported Y, printed Y = reported X.
            bool n = EightPlayersPlugin.ZeroTwoNintendoLabels.Value;
            string printedA = n ? "B" : "A", printedB = n ? "A" : "B";
            string printedX = n ? "Y" : "X", printedY = n ? "X" : "Y";
            return new[]
            {
                ("MoveXJ",          Pole.Positive, new[] { "D-Pad Right", "Hat Right", "DPad Right" }),
                ("MoveXJ",          Pole.Negative, new[] { "D-Pad Left", "Hat Left", "DPad Left" }),
                ("MoveYJ",          Pole.Positive, new[] { "D-Pad Up", "Hat Up", "DPad Up" }),
                ("MoveYJ",          Pole.Negative, new[] { "D-Pad Down", "Hat Down", "DPad Down" }),
                ("MenuRightJ",      Pole.Positive, new[] { "D-Pad Right", "Hat Right", "DPad Right" }),
                ("MenuLeftJ",       Pole.Positive, new[] { "D-Pad Left", "Hat Left", "DPad Left" }),
                ("MenuUpJ",         Pole.Positive, new[] { "D-Pad Up", "Hat Up", "DPad Up" }),
                ("MenuDownJ",       Pole.Positive, new[] { "D-Pad Down", "Hat Down", "DPad Down" }),
                ("AttackJ",         Pole.Positive, new[] { printedX }),
                ("InteractJ",       Pole.Positive, new[] { printedA }),
                ("CancelJ",         Pole.Positive, new[] { printedB }),
                ("SpecialAbilityJ", Pole.Positive, new[] { printedY }),
                ("InventoryJ",      Pole.Positive, new[] { "Left Shoulder", "L", "L1" }),
                ("SelectNextJ",     Pole.Positive, new[] { "Right Shoulder", "R", "R1" }),
                ("UseHealthJ",      Pole.Positive, new[] { "View", "Back", "Select" }),
                ("MenuJJ",          Pole.Positive, new[] { "Menu", "Start" }),
            };
        }

        internal static bool IsZeroTwo(Joystick joystick)
        {
            var match = EightPlayersPlugin.ZeroTwoMatch.Value;
            if (string.IsNullOrEmpty(match))
                return false;
            string haystack = ((joystick.name ?? "") + "|" + (joystick.hardwareName ?? "")).ToLowerInvariant();
            foreach (var token in match.ToLowerInvariant().Split(' '))
            {
                if (!haystack.Contains(token))
                    return false;
            }
            return true;
        }

        internal static void Tick()
        {
            if (UnityEngine.Time.unscaledTime < nextScan)
                return;
            nextScan = UnityEngine.Time.unscaledTime + 3f;
            Apply();
        }

        // Live-debug hook: wipe our bindings and reinstall the layout now.
        internal static void ForceRemap()
        {
            try
            {
                if (!ReInput.isReady)
                    return;
                foreach (var joystick in ReInput.controllers.Joysticks)
                {
                    if (!IsZeroTwo(joystick))
                        continue;
                    foreach (var player in ReInput.players.AllPlayers)
                    {
                        if (!player.controllers.ContainsController(joystick))
                            continue;
                        foreach (var map in player.controllers.maps.GetMaps(ControllerType.Joystick, joystick.id))
                        {
                            foreach (var (actionName, _, _) in GetLayout())
                            {
                                var action = ReInput.mapping.GetAction(actionName);
                                if (action != null)
                                    map.DeleteElementMapsWithAction(action.id);
                            }
                        }
                        MapForPlayer(player, joystick);
                    }
                }
            }
            catch (Exception e)
            {
                EightPlayersPlugin.Log.LogWarning("ZeroTwo ForceRemap failed: " + e);
            }
        }

        internal static void Apply()
        {
            if (!EightPlayersPlugin.ZeroTwoAutoMap.Value)
                return;
            try
            {
                if (!ReInput.isReady)
                    return;
                foreach (var joystick in ReInput.controllers.Joysticks)
                {
                    if (!IsZeroTwo(joystick))
                        continue;
                    foreach (var player in ReInput.players.AllPlayers)
                    {
                        if (player.controllers.ContainsController(joystick))
                            MapForPlayer(player, joystick);
                    }
                }
            }
            catch (Exception e)
            {
                EightPlayersPlugin.Log.LogWarning("ZeroTwo mapping failed: " + e);
            }
        }

        private static void MapForPlayer(Player player, Joystick joystick)
        {
            var log = EightPlayersPlugin.Log;

            var maps = player.controllers.maps.GetMaps(ControllerType.Joystick, joystick.id);
            if (maps == null || maps.Count == 0)
                return;

            // The game enables joystick maps in category "Gamepad"; bind there,
            // falling back to the first map if the category is named differently.
            ControllerMap targetMap = null;
            foreach (var map in maps)
            {
                var cat = ReInput.mapping.GetMapCategory(map.categoryId);
                if (cat != null && cat.name == "Gamepad")
                    targetMap = map;
            }
            targetMap = targetMap ?? maps[0];

            // Self-healing: if our signature binding is still present, nothing to do.
            // (The game can reload saved controller maps over ours at any time.)
            var moveX = ReInput.mapping.GetAction("MoveXJ");
            var dpadRight = FindElement(joystick, new[] { "D-Pad Right", "Hat Right", "DPad Right" });
            if (moveX != null && dpadRight != null)
            {
                foreach (var existing in targetMap.ElementMaps)
                {
                    if (existing.actionId == moveX.id && existing.elementIdentifierId == dpadRight.id)
                        return;
                }
            }

            log.LogInfo($"ZeroTwo: mapping '{joystick.name}' (hw '{joystick.hardwareName}', id {joystick.id}) for player {player.id}");
            if (!mappedJoystickIds.Contains(joystick.id))
            {
                DumpElements(joystick);
                mappedJoystickIds.Add(joystick.id);
            }

            int bound = 0, missing = 0;
            foreach (var (actionName, pole, elementNames) in GetLayout())
            {
                var action = ReInput.mapping.GetAction(actionName);
                if (action == null)
                {
                    log.LogWarning($"ZeroTwo: game has no action '{actionName}'");
                    missing++;
                    continue;
                }
                var element = FindElement(joystick, elementNames);
                if (element == null)
                {
                    log.LogWarning($"ZeroTwo: no element found among [{string.Join(", ", elementNames)}] for {actionName}");
                    missing++;
                    continue;
                }
                // idempotent: our own re-runs and defaults both cleared for this action+pole
                foreach (var existing in new List<ActionElementMap>(targetMap.ElementMaps))
                {
                    if (existing.actionId == action.id && existing.axisContribution == pole)
                        targetMap.DeleteElementMap(existing.id);
                }
                bool ok = targetMap.CreateElementMap(action.id, pole, element.id,
                    ControllerElementType.Button, AxisRange.Full, invert: false);
                if (ok) bound++;
                log.LogInfo($"ZeroTwo: {actionName} ({pole}) <- '{element.name}' [{(ok ? "ok" : "FAILED")}]");
            }
            log.LogInfo($"ZeroTwo: layout installed ({bound} bindings, {missing} missing) on map category {targetMap.categoryId}");
        }

        private static ControllerElementIdentifier FindElement(Joystick joystick, string[] names)
        {
            foreach (var wanted in names)
            {
                foreach (var element in joystick.ElementIdentifiers)
                {
                    if (string.Equals(element.name, wanted, StringComparison.OrdinalIgnoreCase))
                        return element;
                }
            }
            return null;
        }

        private static void DumpElements(Joystick joystick)
        {
            foreach (var element in joystick.ElementIdentifiers)
                EightPlayersPlugin.Log.LogInfo($"ZeroTwo:   element id={element.id} name='{element.name}'");
        }
    }

    [HarmonyPatch(typeof(PlayerControl), "SetTitleControllers")]
    internal static class ZeroTwoTitle_Patch
    {
        private static void Postfix() => ZeroTwoMapping.Apply();
    }

    [HarmonyPatch(typeof(PlayerControl), "SetInGameControllers")]
    internal static class ZeroTwoInGame_Patch
    {
        private static void Postfix() => ZeroTwoMapping.Apply();
    }
}
