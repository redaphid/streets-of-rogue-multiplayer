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
    //
    // Game-state manipulation (GameStateApi; every mutation shows up in the
    // SOR_TRACE behavior trace, which is how tests assert on it):
    //   state                         level/seed/agent summary
    //   agents                        list live agents (uid, type, pos, hp)
    //   spawnagent <type> <x> <y>     spawn an NPC (e.g. spawnagent Thief 10 12)
    //   hp <uid> <delta>              change health (negative damages)
    //   kill <uid>                    kill an agent
    //   give <uid> <item> [count]     add inventory item
    //   drop <uid> <item>             drop inventory item
    //   tp <uid> <x> <y>              teleport an agent
    //   opendoor <uid>                open a door by UID
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
                case "state": Out(GameStateApi.Summary()); break;
                case "ecs":
                    Out(EcsNet.EcsNetManager.Instance != null
                        ? EcsNet.EcsNetManager.Instance.DebugDump()
                        : "no EcsNetManager");
                    break;
                case "screenshot":
                {
                    var path = parts.Length > 1 ? cmd.Substring("screenshot ".Length).Trim() : $"screenshot-{DateTime.Now:HHmmss}.png";
                    UnityEngine.ScreenCapture.CaptureScreenshot(path);
                    Out($"screenshot requested -> {path} (written by the game next frame)");
                    break;
                }
                case "record":
                {
                    // record <seconds> <fps> [dirname] — frame sequence via the
                    // game's own framebuffer (compositor-independent); encode
                    // externally with ffmpeg. Paths resolve to the game's
                    // <Data> dir; the dir must already exist.
                    float secs = float.Parse(parts[1]);
                    int fps = int.Parse(parts[2]);
                    string dir = parts.Length > 3 ? parts[3] : "rec";
                    EightPlayersPlugin.Instance.StartCoroutine(RecordFrames(secs, fps, dir));
                    Out($"recording {secs}s at {fps}fps into {dir}/");
                    break;
                }
                case "room":
                    EcsNet.EcsNetManager.Instance?.JoinRoom(parts[1]);
                    Out($"joining room {parts[1].ToUpperInvariant()}");
                    break;
                case "leave":
                    EcsNet.EcsNetManager.Instance?.LeaveRoom();
                    Out("left room");
                    break;
                case "npcs":
                    if (EcsNet.EcsNetManager.Instance != null)
                        foreach (var line in EcsNet.EcsNetManager.Instance.DescribeNpcRegistry())
                            Out(line);
                    break;
                case "agents":
                    foreach (var agent in GameStateApi.Agents())
                        Out("  " + GameStateApi.DescribeAgent(agent));
                    break;
                case "spawnagent":
                {
                    var spawned = GameStateApi.SpawnAgent(parts[1], ParseVec(parts[2], parts[3]));
                    Out($"spawned {GameStateApi.DescribeAgent(spawned)}");
                    break;
                }
                case "hp":
                {
                    var hp = GameStateApi.ChangeHealth(int.Parse(parts[1]), float.Parse(parts[2]));
                    Out($"agent {parts[1]} health now {hp:0.#}");
                    break;
                }
                case "kill":
                    GameStateApi.Kill(int.Parse(parts[1]));
                    Out($"agent {parts[1]} killed");
                    break;
                case "status":
                {
                    bool on = parts.Length < 4 || parts[3] != "off";
                    GameStateApi.SetStatus(int.Parse(parts[1]), parts[2], on);
                    Out($"agent {parts[1]} status {parts[2]} {(on ? "on" : "off")}");
                    break;
                }
                case "statuses":
                    Out($"agent {parts[1]} statuses: {string.Join(",", new List<string>(GameStateApi.Statuses(int.Parse(parts[1]))).ToArray())}");
                    break;
                case "give":
                    GameStateApi.GiveItem(int.Parse(parts[1]), parts[2], parts.Length > 3 ? int.Parse(parts[3]) : 1);
                    Out($"gave {parts[2]} to agent {parts[1]}");
                    break;
                case "drop":
                    GameStateApi.DropItem(int.Parse(parts[1]), parts[2]);
                    Out($"agent {parts[1]} dropped {parts[2]}");
                    break;
                case "equip":
                    GameStateApi.EquipWeapon(int.Parse(parts[1]), parts[2]);
                    Out($"agent {parts[1]} equipped {parts[2]}");
                    break;
                case "tp":
                    GameStateApi.Teleport(int.Parse(parts[1]), ParseVec(parts[2], parts[3]));
                    Out($"agent {parts[1]} teleported to {parts[2]},{parts[3]}");
                    break;
                case "opendoor":
                    GameStateApi.OpenDoor(int.Parse(parts[1]),
                        parts.Length > 2 ? GameStateApi.FindAgent(int.Parse(parts[2])) : null);
                    Out($"door {parts[1]} opened{(parts.Length > 2 ? $" by agent {parts[2]}" : "")}");
                    break;
                case "lockdoor":
                {
                    bool nowLocked = GameStateApi.LockDoor(int.Parse(parts[1]), parts.Length < 3 || parts[2] != "off");
                    Out($"door {parts[1]} locked={nowLocked}");
                    break;
                }
                case "objects":
                {
                    var gc = GameController.gameController;
                    UnityEngine.Vector2 origin = gc?.playerAgent != null ? (UnityEngine.Vector2)gc.playerAgent.tr.position : UnityEngine.Vector2.zero;
                    var objs = new List<ObjectReal>(GameStateApi.Objects());
                    objs.RemoveAll(o => o.tr == null || o is Door);
                    objs.Sort((a, b) =>
                        ((UnityEngine.Vector2)a.tr.position - origin).sqrMagnitude
                        .CompareTo(((UnityEngine.Vector2)b.tr.position - origin).sqrMagnitude));
                    for (int i = 0; i < objs.Count && i < 15; i++)
                    {
                        var o = objs[i];
                        UnityEngine.Vector2 p = o.tr.position;
                        Out($"  object uid={o.UID} '{o.objectName}' pos=({p.x:0.#},{p.y:0.#}) destroying={o.destroying}");
                    }
                    break;
                }
                case "destroyobj":
                    GameStateApi.DestroyObject(int.Parse(parts[1]));
                    Out($"object {parts[1]} destroyed");
                    break;
                case "containers":
                {
                    var gc = GameController.gameController;
                    UnityEngine.Vector2 origin = gc?.playerAgent != null ? (UnityEngine.Vector2)gc.playerAgent.tr.position : UnityEngine.Vector2.zero;
                    var cs = new List<ObjectReal>(GameStateApi.Containers());
                    cs.Sort((a, b) =>
                        ((UnityEngine.Vector2)a.tr.position - origin).sqrMagnitude
                        .CompareTo(((UnityEngine.Vector2)b.tr.position - origin).sqrMagnitude));
                    for (int i = 0; i < cs.Count && i < 10; i++)
                    {
                        UnityEngine.Vector2 p = cs[i].tr.position;
                        Out($"  container uid={cs[i].UID} '{cs[i].objectName}' pos=({p.x:0.#},{p.y:0.#}) items={cs[i].objectInvDatabase.InvItemList.Count}");
                    }
                    break;
                }
                case "chestgive":
                    GameStateApi.ChestGive(ParseVec(parts[1], parts[2]), parts[3]);
                    Out($"container at {parts[1]},{parts[2]} given {parts[3]}");
                    break;
                case "shoptake":
                    GameStateApi.ShopTake(int.Parse(parts[1]), parts[2]);
                    Out($"took {parts[2]} from agent {parts[1]}");
                    break;
                case "chesttake":
                    GameStateApi.ChestTake(ParseVec(parts[1], parts[2]), parts[3]);
                    Out($"took {parts[3]} from container at {parts[1]},{parts[2]}");
                    break;
                case "chestitems":
                {
                    var chest = GameStateApi.FindContainerAt(ParseVec(parts[1], parts[2]));
                    if (chest == null) { Out("no container there"); break; }
                    foreach (var it in chest.objectInvDatabase.InvItemList)
                        Out($"  item '{it?.invItemName}' x{it?.invItemCount}");
                    Out($"container '{chest.objectName}': {chest.objectInvDatabase.InvItemList.Count} item(s)");
                    break;
                }
                case "fires":
                {
                    int n = 0;
                    foreach (var fire in GameStateApi.Fires())
                    {
                        if (fire.tr == null) continue;
                        UnityEngine.Vector2 p = fire.tr.position;
                        Out($"  fire pos=({p.x:0.#},{p.y:0.#}) destroying={fire.destroying}");
                        if (++n >= 15) break;
                    }
                    Out($"fires: {n} shown");
                    break;
                }
                case "ignite":
                    GameStateApi.Ignite(ParseVec(parts[1], parts[2]));
                    Out($"fire ignited at {parts[1]},{parts[2]}");
                    break;
                case "extinguish":
                    GameStateApi.Extinguish(ParseVec(parts[1], parts[2]));
                    Out($"fire extinguished at {parts[1]},{parts[2]}");
                    break;
                case "worldhash":
                {
                    var gc = GameController.gameController;
                    Out($"worldhash {GameStateApi.WorldHash():x8} seed={gc?.loadLevel?.randomSeedNum} level={gc?.sessionDataBig?.curLevel}");
                    break;
                }
                case "entities":
                {
                    var mgr = EcsNet.EcsNetManager.Instance;
                    if (mgr == null) { Out("no EcsNetManager"); break; }
                    int n = 0;
                    foreach (var line in mgr.HarnessEntities()) { Out("  " + line); n++; }
                    Out($"entities: {n}");
                    break;
                }
                case "ecsget":
                {
                    var json = EcsNet.EcsNetManager.Instance?.HarnessGet(int.Parse(parts[1]));
                    Out(json ?? $"no entity {parts[1]}");
                    break;
                }
                case "ecsset":
                {
                    // ecsset <entity> <json>  (json must contain no spaces)
                    EcsNet.EcsNetManager.Instance?.HarnessSet(int.Parse(parts[1]),
                        Newtonsoft.Json.Linq.JObject.Parse(cmd.Substring(cmd.IndexOf(parts[1]) + parts[1].Length).Trim()));
                    Out($"set sent for entity {parts[1]}");
                    break;
                }
                case "ecsevent":
                {
                    // ecsevent <name> [json]
                    var data = parts.Length > 2
                        ? Newtonsoft.Json.Linq.JObject.Parse(cmd.Substring(cmd.IndexOf(parts[2])))
                        : new Newtonsoft.Json.Linq.JObject();
                    EcsNet.EcsNetManager.Instance?.HarnessEvent(parts[1], data);
                    Out($"event '{parts[1]}' sent");
                    break;
                }
                case "move":
                    VirtualInput.Move(ParseVec(parts[1], parts[2]), parts.Length > 3 ? float.Parse(parts[3]) : 1f);
                    Out($"moving ({parts[1]},{parts[2]}) for {(parts.Length > 3 ? parts[3] : "1")}s");
                    break;
                case "walkto":
                    VirtualInput.WalkTo(ParseVec(parts[1], parts[2]), parts.Length > 3 ? float.Parse(parts[3]) : 15f);
                    Out($"walking to ({parts[1]},{parts[2]})");
                    break;
                case "hold":
                    VirtualInput.Hold(parts[1], parts.Length > 2 ? float.Parse(parts[2]) : 0.5f);
                    Out($"holding {parts[1]}");
                    break;
                case "stop":
                    VirtualInput.Stop();
                    Out("virtual input cleared");
                    break;
                case "input":
                    Out(VirtualInput.Describe());
                    break;
                case "doors":
                {
                    var gc = GameController.gameController;
                    UnityEngine.Vector2 origin = gc?.playerAgent != null ? (UnityEngine.Vector2)gc.playerAgent.tr.position : UnityEngine.Vector2.zero;
                    var doors = new List<Door>(GameStateApi.Doors());
                    doors.Sort((a, b) =>
                        ((UnityEngine.Vector2)a.tr.position - origin).sqrMagnitude
                        .CompareTo(((UnityEngine.Vector2)b.tr.position - origin).sqrMagnitude));
                    for (int i = 0; i < doors.Count && i < 10; i++)
                    {
                        var d = doors[i];
                        UnityEngine.Vector2 p = d.tr.position;
                        Out($"  door uid={d.UID} type={d.doorType} open={d.open} pos=({p.x:0.#},{p.y:0.#})");
                    }
                    break;
                }
                case "nextlevel":
                    GameController.gameController.loadLevel.NextLevel();
                    Out("next level triggered");
                    break;
                case "items":
                {
                    var gc = GameController.gameController;
                    UnityEngine.Vector2 origin = gc?.playerAgent != null ? (UnityEngine.Vector2)gc.playerAgent.tr.position : UnityEngine.Vector2.zero;
                    var items = new List<Item>(GameStateApi.GroundItems());
                    items.Sort((a, b) =>
                        ((UnityEngine.Vector2)a.tr.position - origin).sqrMagnitude
                        .CompareTo(((UnityEngine.Vector2)b.tr.position - origin).sqrMagnitude));
                    for (int i = 0; i < items.Count && i < 10; i++)
                    {
                        UnityEngine.Vector2 p = items[i].tr.position;
                        Out($"  item '{items[i].invItem?.invItemName}' pos=({p.x:0.#},{p.y:0.#})");
                    }
                    break;
                }
                case "pickup":
                    // pickup <agentUid> <x> <y> <itemName>
                    GameStateApi.PickUpGroundItem(int.Parse(parts[1]), ParseVec(parts[2], parts[3].Split(' ')[0]),
                        parts[3].Contains(" ") ? parts[3].Substring(parts[3].IndexOf(' ') + 1) : null);
                    Out("pickup attempted");
                    break;
                default: Out("unknown command"); break;
            }
        }

        private static Vector2 ParseVec(string x, string y) =>
            new Vector2(float.Parse(x), float.Parse(y));

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

        private static System.Collections.IEnumerator RecordFrames(float seconds, int fps, string dir)
        {
            int total = (int)(seconds * fps);
            var wait = new UnityEngine.WaitForSecondsRealtime(1f / fps);
            for (int i = 0; i < total; i++)
            {
                UnityEngine.ScreenCapture.CaptureScreenshot($"{dir}/f{i:D5}.png");
                yield return wait;
            }
            Out($"recording done: {total} frames in {dir}/");
        }

        private static void Out(string line)
        {
            EightPlayersPlugin.Log.LogInfo("EPCMD " + line);
            try { File.AppendAllText(OutPath, line + "\n"); } catch { }
        }
    }
}
