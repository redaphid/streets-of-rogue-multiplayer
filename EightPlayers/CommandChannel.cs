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
    //   say <uid> <text>              pop the agent's in-game speech bubble
    //   drop <uid> <item>             drop inventory item
    //   tp <uid> <x> <y>              teleport an agent
    //   opendoor <uid>                open a door by UID
    //   inventory <uid>               one-shot JSON inventory listing
    //   nearby <x> <y> <radius>       agents + objects within radius (JSON)
    //   walknpc <uid> <x> <y>         EXPERIMENTAL: NPC walks via own pathfinding
    //   aimarker <uid> <on|off>       cosmetic cyan glow marking AI-driven agents
    //   setgoal <uid> <goal> [target] inject a REAL brain goal (Follow/Guard/
    //                                 Battle/Flee/Investigate/Wander/WanderFar;
    //                                 target = uid|player alias or x,y)
    //   setmenu <uid> <b64json>       custom NPC talk menu; b64(["opt",...]) ≤6×40;
    //                                 selections stream as menu_choice events
    //   clearmenu <uid|all>           restore the vanilla talk menu
    //   label <uid|player[:n]> <TEXT...>  quest-marker-style world-space text
    //                                 pinned over an agent OR object (uid may
    //                                 be an ObjectReal uid); text = rest of
    //                                 line, case preserved (vanilla style is
    //                                 ALL CAPS), ≤48 chars
    //   clearlabel <uid|all>          remove label(s)
    //   labels                        list active labels
    //
    // Code mode (BehaviorEngine.cs — Lua scripts run in-game at frame rate):
    //   behavior <uid|player[:n]> <lua...>     install/replace (script = rest of line)
    //   behaviorb64 <uid> <base64> [hz]        same, base64-encoded (newlines survive)
    //   behaviors                              list {uid,hz,errors,enabled,bytes}
    //   clearbehavior <uid|all>                remove behavior(s)
    //
    // Reflection (Reflect.cs; targets: gc | agent:<uid> | player[:<n>] |
    // handle:<id> | static:<Type>):
    //   inspect/get/set/call/find/types, members <Type> [kind] [nameFilter],
    //   getmany <target> <p1>|<p2>|..., keys <target> <path>
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
                RunLine(line);
        }

        /// <summary>One command line through the EXACT same path the file
        /// channel uses (echo, execute, error formatting); output goes to the
        /// current sink (ep_out.txt, or a capture buffer for the HTTP channel).
        /// MUST be called on the Unity main thread.</summary>
        private static void RunLine(string line)
        {
            var cmd = line.Trim();
            if (cmd.Length == 0 || cmd.StartsWith("#"))
                return;
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

        /// <summary>Execute command line(s) and return the reply text that the
        /// file channel would have appended to ep_out.txt (including the
        /// "&gt; cmd" echo line). Used by HttpChannel; main thread only.</summary>
        internal static string ExecuteCaptured(string text)
        {
            var sb = new StringBuilder();
            _capture = sb;
            try
            {
                foreach (var line in text.Split('\n'))
                    RunLine(line);
            }
            finally
            {
                _capture = null;
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static StringBuilder _capture;

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
                    var hp = GameStateApi.ChangeHealth(GameStateApi.ResolveUid(parts[1]), float.Parse(parts[2]));
                    Out($"agent {parts[1]} health now {hp:0.#}");
                    break;
                }
                case "kill":
                    GameStateApi.Kill(GameStateApi.ResolveUid(parts[1]));
                    Out($"agent {parts[1]} killed");
                    break;
                case "status":
                {
                    bool on = parts.Length < 4 || parts[3] != "off";
                    GameStateApi.SetStatus(GameStateApi.ResolveUid(parts[1]), parts[2], on);
                    Out($"agent {parts[1]} status {parts[2]} {(on ? "on" : "off")}");
                    break;
                }
                case "statuses":
                    Out($"agent {parts[1]} statuses: {string.Join(",", new List<string>(GameStateApi.Statuses(GameStateApi.ResolveUid(parts[1]))).ToArray())}");
                    break;
                case "say":
                {
                    // say <uid> <text...> — pop the agent's in-game speech
                    // bubble. Text is the rest of the line (spaces allowed).
                    var sayParts = cmd.Split(new[] { ' ' }, 3);
                    if (sayParts.Length < 3) { Out("usage: say <uid> <text>"); break; }
                    GameStateApi.Say(GameStateApi.ResolveUid(sayParts[1]), sayParts[2]);
                    Out($"agent {sayParts[1]} said \"{sayParts[2]}\"");
                    break;
                }
                case "give":
                    GameStateApi.GiveItem(GameStateApi.ResolveUid(parts[1]), parts[2], parts.Length > 3 ? int.Parse(parts[3]) : 1);
                    Out($"gave {parts[2]} to agent {parts[1]}");
                    break;
                case "drop":
                    GameStateApi.DropItem(GameStateApi.ResolveUid(parts[1]), parts[2]);
                    Out($"agent {parts[1]} dropped {parts[2]}");
                    break;
                case "equip":
                    GameStateApi.EquipWeapon(GameStateApi.ResolveUid(parts[1]), parts[2]);
                    Out($"agent {parts[1]} equipped {parts[2]}");
                    break;
                case "tp":
                    GameStateApi.Teleport(GameStateApi.ResolveUid(parts[1]), ParseVec(parts[2], parts[3]));
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
                    GameStateApi.ShopTake(GameStateApi.ResolveUid(parts[1]), parts[2]);
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
                case "spawngas":
                {
                    var src = GameStateApi.FindObjectReal(int.Parse(parts[1]));
                    if (src == null) { Out($"no object {parts[1]}"); break; }
                    GameStateApi.SpawnGas(src, src.tr.position, parts.Length > 2 ? parts[2] : "Flammable");
                    Out($"gas spawned at object {parts[1]}");
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
                case "reloadlevel":
                    LoadWatchdog.ForceReload();
                    Out("level reload forced (watchdog path)");
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
                    GameStateApi.PickUpGroundItem(GameStateApi.ResolveUid(parts[1]), ParseVec(parts[2], parts[3].Split(' ')[0]),
                        parts[3].Contains(" ") ? parts[3].Substring(parts[3].IndexOf(' ') + 1) : null);
                    Out("pickup attempted");
                    break;
                // ---- reshape the world (GM verbs) ---------------------------
                case "explode":
                {
                    var ex = GameStateApi.Explode(ParseVec(parts[1], parts[2]), parts.Length > 3 ? parts[3] : "Normal");
                    Out($"explosion at {parts[1]},{parts[2]}{(ex == null ? " (null)" : "")}");
                    break;
                }
                case "spawnobject":
                {
                    // spawnobject <name> <x> <y>
                    var obj = GameStateApi.SpawnObject(parts[1], ParseVec(parts[2], parts[3]));
                    Out(obj != null ? $"spawned object uid={obj.UID} '{obj.objectName}'" : "spawn returned null");
                    break;
                }
                case "destroywall":
                    GameStateApi.DestroyWall(ParseVec(parts[1], parts[2]));
                    Out($"wall destroyed at {parts[1]},{parts[2]}");
                    break;
                case "buildwall":
                    GameStateApi.BuildWall(ParseVec(parts[1], parts[2]));
                    Out($"wall built at {parts[1]},{parts[2]}");
                    break;
                case "gascloud":
                {
                    var gas = GameStateApi.GasCloud(ParseVec(parts[1], parts[2]), parts.Length > 3 ? parts[3] : "Poison");
                    Out($"gas cloud at {parts[1]},{parts[2]}{(gas == null ? " (vent spawned; venting momentarily)" : "")}");
                    break;
                }
                case "recruit":
                    GameStateApi.Recruit(GameStateApi.ResolveUid(parts[1]));
                    Out($"agent {parts[1]} recruited into the party");
                    break;
                case "setmenu":
                    // setmenu <uid> <b64json> — custom interaction menu on an
                    // NPC (DialogueMenu.cs); json = ["opt1","opt2",...] (≤6
                    // options, ≤40 chars each; [] flags the uid with "...").
                    // Selections stream as menu_choice events on GET /events.
                    Out(DialogueMenuCore.SetMenu(GameStateApi.ResolveUid(parts[1]), parts[2]));
                    break;
                case "clearmenu":
                    // clearmenu <uid|all> — back to the vanilla talk menu
                    Out(parts[1] == "all"
                        ? $"cleared {DialogueMenuCore.ClearAll()} menu(s)"
                        : DialogueMenuCore.Clear(GameStateApi.ResolveUid(parts[1])));
                    break;
                case "label":
                {
                    // label <uid|player[:n]> <TEXT...> — pin quest-marker-style
                    // world-space text over an agent or object (rogue-gm#17).
                    // Text is the rest of the line; case preserved.
                    var lp = cmd.Split(new[] { ' ' }, 3);
                    if (lp.Length < 3) { Out("usage: label <uid|player[:n]> <TEXT...>"); break; }
                    Out(Labels.Apply(GameStateApi.ResolveUid(lp[1]), lp[2]));
                    break;
                }
                case "clearlabel":
                    // clearlabel <uid|all>
                    Out(parts[1] == "all"
                        ? $"cleared {Labels.ClearAll()} label(s)"
                        : Labels.Clear(GameStateApi.ResolveUid(parts[1])));
                    break;
                case "labels":
                    Out(Labels.Summary());
                    break;
                case "inventory":
                    // inventory <uid> — one-shot JSON inventory listing
                    Out(GameStateApi.InventoryJson(GameStateApi.ResolveUid(parts[1])));
                    break;
                case "nearby":
                    // nearby <x> <y> <radius> — agents + objects within radius
                    Out(GameStateApi.NearbyJson(ParseVec(parts[1], parts[2]), float.Parse(parts[3])));
                    break;
                case "walknpc":
                    // walknpc <uid> <x> <y> — EXPERIMENTAL: route an NPC via its
                    // own pathfinding (brain goals may re-route it)
                    GameStateApi.WalkNpc(GameStateApi.ResolveUid(parts[1]), ParseVec(parts[2], parts[3]));
                    Out($"agent {parts[1]} walking to {parts[2]},{parts[3]} (experimental — brain may override)");
                    break;
                case "aimarker":
                {
                    // aimarker <uid|player> <on|off> — purely cosmetic cyan glow
                    // on AI-driven agents (zero gameplay impact; rogue-gm#12)
                    bool markerOn = parts.Length < 3 || parts[2] != "off";
                    Out(GameStateApi.AiMarker(GameStateApi.ResolveUid(parts[1]), markerOn));
                    break;
                }
                case "setgoal":
                {
                    // setgoal <uid|player> <goalName> [<targetUid|player> | <x,y>]
                    // — inject a REAL goal into the NPC's brain so it DOES the
                    // thing (rogue-gm#3 / missing.md §7-4a)
                    int goalTargetUid = 0;
                    Vector2? goalPos = null;
                    if (parts.Length > 3)
                    {
                        var t = parts[3].Trim();
                        if (t.Contains(","))
                        {
                            var xy = t.Split(',');
                            goalPos = new Vector2(float.Parse(xy[0]), float.Parse(xy[1]));
                        }
                        else
                        {
                            goalTargetUid = GameStateApi.ResolveUid(t);
                        }
                    }
                    Out(GameStateApi.SetGoal(GameStateApi.ResolveUid(parts[1]), parts[2], goalTargetUid, goalPos));
                    break;
                }

                // ---- code mode: in-game Lua behaviors ------------------------
                case "behavior":
                {
                    // behavior <uid|player[:n]> <lua...> — script is the rest of
                    // the line (single-line scripts; use behaviorb64 for real ones)
                    var bp = cmd.Split(new[] { ' ' }, 3);
                    if (bp.Length < 3) { Out("usage: behavior <uid|player[:n]> <lua...>"); break; }
                    int buid = GameStateApi.ResolveUid(bp[1]);
                    var err = BehaviorEngine.Install(buid, bp[2], 10f);
                    Out(err ?? $"behavior installed on agent {buid} (10Hz)");
                    break;
                }
                case "behaviorb64":
                {
                    // behaviorb64 <uid|player[:n]> <base64> [hz] — the preferred
                    // form: newlines/pipes survive the one-line channel
                    var bp = cmd.Split(new[] { ' ' }, 4);
                    if (bp.Length < 3) { Out("usage: behaviorb64 <uid|player[:n]> <base64> [hz]"); break; }
                    string lua = Encoding.UTF8.GetString(Convert.FromBase64String(bp[2]));
                    float hz = bp.Length > 3 ? float.Parse(bp[3]) : 10f;
                    int buid = GameStateApi.ResolveUid(bp[1]);
                    var err = BehaviorEngine.Install(buid, lua, hz);
                    Out(err ?? $"behavior installed on agent {buid} ({Encoding.UTF8.GetByteCount(lua)} bytes, {hz:0.#}Hz)");
                    break;
                }
                case "behaviors":
                    Out(BehaviorEngine.List());
                    break;
                case "clearbehavior":
                    if (parts.Length > 1 && parts[1] == "all")
                        Out($"cleared {BehaviorEngine.ClearAll()} behavior(s)");
                    else
                        Out(BehaviorEngine.Clear(GameStateApi.ResolveUid(parts[1]))
                            ? $"behavior cleared on agent {parts[1]}"
                            : $"no behavior on agent {parts[1]}");
                    break;

                // ---- general reflection surface -----------------------------
                case "inspect":
                    Out(Reflect.Inspect(parts[1]));
                    break;
                case "get":
                    Out(Reflect.Get(parts[1], parts[2]));
                    break;
                case "getmany":
                {
                    // getmany <target> <path1>|<path2>|... — batch read, one
                    // round trip (paths = rest of line, pipe-separated)
                    var gm = cmd.Split(new[] { ' ' }, 3);
                    if (gm.Length < 3) { Out("usage: getmany <target> <path1>|<path2>|..."); break; }
                    Out(Reflect.GetMany(gm[1], gm[2]));
                    break;
                }
                case "keys":
                    // keys <target> <path> — string keys of a dictionary member
                    Out(Reflect.Keys(parts[1], parts.Length > 2 ? parts[2] : ""));
                    break;
                case "set":
                {
                    // set <target> <path> <value...> — value is the rest of the line
                    var sp = cmd.Split(new[] { ' ' }, 4);
                    if (sp.Length < 4) { Out("usage: set <target> <path> <value>"); break; }
                    Out(Reflect.Set(sp[1], sp[2], sp[3]));
                    break;
                }
                case "call":
                {
                    // call <target> <method> [jsonArgs...] — json is the rest of the line
                    var cp = cmd.Split(new[] { ' ' }, 4);
                    Out(Reflect.Call(cp[1], cp[2], cp.Length > 3 ? cp[3] : null));
                    break;
                }
                case "find":
                    Out(Reflect.Find(parts[1], parts.Length > 2 ? int.Parse(parts[2]) : 25));
                    break;
                case "members":
                    // members <TypeName> [fields|props|methods|all] [nameFilter]
                    Out(Reflect.Members(parts[1],
                        parts.Length > 2 ? parts[2] : "all",
                        parts.Length > 3 ? parts[3] : null));
                    break;
                case "types":
                    Out(Reflect.Types(parts[1], parts.Length > 2 ? int.Parse(parts[2]) : 50));
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
            // HTTP channel capture: replies for HTTP-submitted verbs go back on
            // the HTTP response, not into the shared ep_out.txt mailbox.
            if (_capture != null)
            {
                _capture.Append(line).Append('\n');
                return;
            }
            try { File.AppendAllText(OutPath, line + "\n"); } catch { }
        }
    }
}
