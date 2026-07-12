using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Linq;
using UnityEngine;
// The game assembly has its own global `Table` type (a furniture object) —
// alias MoonSharp's to avoid the collision.
using LuaTable = MoonSharp.Interpreter.Table;

namespace EightPlayers
{
    // "Code mode": per-agent Lua behaviors executed INSIDE the game loop.
    // Claude (or any channel client) installs a small script on an agent via
    // the `behavior`/`behaviorb64` verbs; the engine calls the script's global
    // `tick(api)` at ~10Hz (configurable per behavior) from Plugin.Update —
    // reflexes at frame rate, no LLM/channel round trip in the loop.
    //
    // Scripting: MoonSharp (pure-C# Lua 5.2), CoreModules.Preset_HardSandbox
    // (no io/os/require). Each behavior gets its own Script instance, so
    // top-level locals and globals persist across ticks and never leak
    // between agents. A persistent `mem` table is provided as the documented
    // state home (cleared on script replace).
    //
    // Lua API (registered as globals AND collected in the `api` table passed
    // to tick):
    //   self()                 -> {uid,x,y,hp,hpmax,dead}
    //   nearby(radius)         -> array of {uid,name,type,x,y,hp,dead,isPlayer},
    //                             distance-sorted, self excluded (shared
    //                             per-frame snapshot — cheap to call)
    //   player(n?)             -> live player-N table (same shape) or nil
    //   dist(x1,y1,x2,y2)      -> number
    //   moveToward(x,y)        one pathfinding step (SetFinalDestPosition —
    //                          the agent walks there via its own pathfinding)
    //   teleport(x,y)          instant reposition (use sparingly)
    //   say(text)              speech bubble; rate-limited to 1 per 2s per
    //                          agent (extra calls dropped; returns false)
    //   hp(uid,delta)          damage(-)/heal(+) a target within 2.5 tiles of
    //                          self; returns false if out of range/missing
    //   attackNearest(sub?,d?) hit the nearest live matching agent within 2.5
    //                          tiles (type/name substring, default 5 dmg);
    //                          returns its uid or nil
    //   status(effect,on)      status effect on SELF only
    //   setGoal(name,target?)  inject a REAL brain goal on SELF (Follow/Guard/
    //                          Battle/Flee/Investigate/Wander/WanderFar;
    //                          target = uid for the agent-typed goals);
    //                          returns the reply string ("error: ..." on failure)
    //   takeControl(on)        on=true disables the native brain (the
    //                          NpcSync.SetBrain list surgery) so THIS behavior
    //                          fully owns movement — moveToward still works
    //                          while brain.active=false (PathfindingAI honors
    //                          finalDestPosition without the brain); on=false
    //                          hands the body back to the game's AI
    //   mem                    persistent table (survives ticks)
    //   time()                 -> game seconds (Time.time)
    //
    // Safety: script errors are caught, logged once per distinct message (not
    // per tick), and auto-disable the behavior after 5 consecutive failures.
    // Runaway loops are pre-empted via MoonSharp's forced-yield instruction
    // counter (the tick runs as a CLR-side coroutine with AutoYieldCounter);
    // a forced yield disables the behavior immediately. Scripts that are slow
    // but not looping (>2ms/tick, wall clock) are disabled after 10
    // consecutive slow ticks.
    internal static class BehaviorEngine
    {
        private const float DefaultHz = 10f;
        private const int MaxConsecutiveErrors = 5;
        private const double TickBudgetMs = 2.0;
        private const int SlowTicksBeforeDisable = 10;
        private const long TickInstructionCap = 250_000;
        private const long LoadInstructionCap = 1_000_000;
        private const int MissingTicksBeforeRemove = 50; // ~5s at 10Hz — uid churned
        private const float SayCooldownSeconds = 2f;
        private const float MeleeRange = 2.5f;

        private class Behavior
        {
            public int Uid;
            public string Source;
            public Script Script;
            public DynValue TickFn;
            public DynValue ApiTable;
            public float Hz;
            public float NextDue;
            public int ConsecutiveErrors;
            public int TotalErrors;
            public int SlowTicks;
            public int MissingTicks;
            public bool Enabled = true;
            public string LastLoggedError;
            public float LastSay = float.NegativeInfinity;
            public string LastLabel; // dedupe: skip marker rebuild when text unchanged
            public Agent Agent; // resolved fresh each engine tick
        }

        private struct AgentSnap
        {
            public int Uid;
            public string Name, Type;
            public float X, Y, Hp, HpMax;
            public bool Dead, IsPlayer;
        }

        private static readonly Dictionary<int, Behavior> _behaviors = new Dictionary<int, Behavior>();
        private static readonly List<int> _removals = new List<int>();
        // One agent scan shared by every behavior that runs this frame — API
        // calls (nearby/attackNearest) filter this instead of rescanning.
        private static readonly List<AgentSnap> _snapshot = new List<AgentSnap>();
        private static int _snapshotFrame = -1;
        private static readonly Stopwatch _watch = new Stopwatch();

        // ---- verb surface ----------------------------------------------------

        /// <summary>Install or replace the behavior on an agent. Returns null on
        /// success, or a compile/validation error message.</summary>
        internal static string Install(int uid, string luaSource, float hz)
        {
            if (GameStateApi.FindAgent(uid) == null)
                return $"no agent with uid {uid}";
            hz = Mathf.Clamp(hz <= 0f ? DefaultHz : hz, 0.1f, 60f);

            var script = new Script(CoreModules.Preset_HardSandbox);
            script.Options.DebugPrint = s => EightPlayersPlugin.Log.LogInfo($"[lua {uid}] {s}");

            var b = new Behavior { Uid = uid, Source = luaSource, Script = script, Hz = hz };
            RegisterApi(b);

            DynValue chunk;
            try
            {
                chunk = script.LoadString(luaSource, null, $"behavior:{uid}");
            }
            catch (SyntaxErrorException e)
            {
                return $"lua compile error: {e.DecoratedMessage ?? e.Message}";
            }

            // Run the top-level chunk once (defines tick, may set up state) —
            // guarded against runaway loops the same way ticks are.
            try
            {
                var co = script.CreateCoroutine(chunk);
                co.Coroutine.AutoYieldCounter = LoadInstructionCap;
                var result = co.Coroutine.Resume();
                if (result.Type == DataType.YieldRequest || co.Coroutine.State == CoroutineState.ForceSuspended)
                    return "lua load error: top-level chunk exceeded the instruction budget (runaway loop?)";
            }
            catch (InterpreterException e)
            {
                return $"lua load error: {e.DecoratedMessage ?? e.Message}";
            }

            b.TickFn = script.Globals.Get("tick");
            if (b.TickFn.Type != DataType.Function)
                return "script must define a global function tick(api)";

            _behaviors[uid] = b; // replace wipes the old script + mem wholesale
            return null;
        }

        internal static bool Clear(int uid) => _behaviors.Remove(uid);

        internal static int ClearAll()
        {
            int n = _behaviors.Count;
            _behaviors.Clear();
            return n;
        }

        /// <summary>JSON list: [{uid, hz, errors, enabled, bytes}].</summary>
        internal static string List()
        {
            var arr = new JArray();
            foreach (var b in _behaviors.Values)
                arr.Add(new JObject
                {
                    ["uid"] = b.Uid,
                    ["hz"] = Mathf.Round(b.Hz * 10f) / 10f,
                    ["errors"] = b.TotalErrors,
                    ["enabled"] = b.Enabled,
                    ["bytes"] = Encoding.UTF8.GetByteCount(b.Source),
                });
            return arr.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ---- engine tick (Plugin.Update, main thread) ------------------------

        internal static void Tick()
        {
            if (_behaviors.Count == 0)
                return;
            var gc = GameController.gameController;
            if (gc == null || !gc.loadCompleteReally)
                return; // mid level-load: don't tick, don't count agents missing
            float now = Time.time;
            foreach (var b in _behaviors.Values)
            {
                if (!b.Enabled || now < b.NextDue)
                    continue;
                b.NextDue = now + 1f / b.Hz;
                b.Agent = GameStateApi.FindAgent(b.Uid);
                if (b.Agent == null)
                {
                    // uids churn per level — after ~5s of a settled level with
                    // no such agent, the body is gone for good.
                    if (++b.MissingTicks >= MissingTicksBeforeRemove)
                        _removals.Add(b.Uid);
                    continue;
                }
                b.MissingTicks = 0;
                EnsureSnapshot(gc);
                RunTick(b, now);
            }
            if (_removals.Count > 0)
            {
                foreach (var uid in _removals)
                {
                    _behaviors.Remove(uid);
                    EightPlayersPlugin.Log.LogInfo($"behavior {uid}: agent gone (level change or despawn) — behavior removed");
                }
                _removals.Clear();
            }
        }

        private static void RunTick(Behavior b, float now)
        {
            _watch.Restart();
            try
            {
                var co = b.Script.CreateCoroutine(b.TickFn);
                co.Coroutine.AutoYieldCounter = TickInstructionCap;
                var result = co.Coroutine.Resume(b.ApiTable);
                if (result.Type == DataType.YieldRequest || co.Coroutine.State == CoroutineState.ForceSuspended)
                {
                    b.Enabled = false;
                    b.TotalErrors++;
                    EightPlayersPlugin.Log.LogWarning($"behavior {b.Uid}: tick exceeded the instruction budget (runaway loop?) — DISABLED");
                    return;
                }
                b.ConsecutiveErrors = 0;
            }
            catch (InterpreterException e)
            {
                Fail(b, e.DecoratedMessage ?? e.Message);
            }
            catch (Exception e)
            {
                Fail(b, $"{e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _watch.Stop();
            }
            if (_watch.Elapsed.TotalMilliseconds > TickBudgetMs)
            {
                if (++b.SlowTicks >= SlowTicksBeforeDisable)
                {
                    b.Enabled = false;
                    EightPlayersPlugin.Log.LogWarning(
                        $"behavior {b.Uid}: >{TickBudgetMs}ms/tick for {b.SlowTicks} consecutive ticks — DISABLED");
                }
            }
            else
            {
                b.SlowTicks = 0;
            }
        }

        private static void Fail(Behavior b, string message)
        {
            b.TotalErrors++;
            b.ConsecutiveErrors++;
            if (message != b.LastLoggedError) // once per distinct error, not per tick
            {
                b.LastLoggedError = message;
                EightPlayersPlugin.Log.LogWarning($"behavior {b.Uid}: {message} (repeats muted)");
            }
            if (b.ConsecutiveErrors >= MaxConsecutiveErrors)
            {
                b.Enabled = false;
                EightPlayersPlugin.Log.LogWarning($"behavior {b.Uid}: {b.ConsecutiveErrors} consecutive errors — DISABLED");
            }
        }

        // ---- shared per-frame agent snapshot ----------------------------------

        private static void EnsureSnapshot(GameController gc)
        {
            if (_snapshotFrame == Time.frameCount)
                return;
            _snapshotFrame = Time.frameCount;
            _snapshot.Clear();
            foreach (var a in GameStateApi.Agents())
            {
                if (a.tr == null)
                    continue;
                Vector2 p = a.tr.position;
                _snapshot.Add(new AgentSnap
                {
                    Uid = a.UID,
                    Name = string.IsNullOrEmpty(a.agentRealName) ? a.agentName : a.agentRealName,
                    Type = a.agentName,
                    X = p.x,
                    Y = p.y,
                    Hp = a.health,
                    HpMax = a.healthMax,
                    Dead = a.dead,
                    IsPlayer = a.isPlayer != 0, // vanilla: int player number, 0 = NPC
                });
            }
        }

        // ---- the Lua API -------------------------------------------------------

        private static void RegisterApi(Behavior b)
        {
            var script = b.Script;
            var api = new LuaTable(script);

            void Reg(string name, object fn)
            {
                var dv = DynValue.FromObject(script, fn);
                script.Globals[name] = dv;
                api[name] = dv;
            }

            var mem = new LuaTable(script);
            script.Globals["mem"] = mem;
            api["mem"] = mem;

            Reg("self", (Func<LuaTable>)(() => AgentTable(b, b.Agent)));

            Reg("nearby", (Func<double, LuaTable>)(radius =>
            {
                var arr = new LuaTable(script);
                var me = b.Agent;
                if (me == null || me.tr == null)
                    return arr;
                Vector2 p = me.tr.position;
                float r2 = (float)(radius * radius);
                var hits = new List<KeyValuePair<float, AgentSnap>>();
                foreach (var s in _snapshot)
                {
                    if (s.Uid == b.Uid)
                        continue;
                    float d2 = (s.X - p.x) * (s.X - p.x) + (s.Y - p.y) * (s.Y - p.y);
                    if (d2 <= r2)
                        hits.Add(new KeyValuePair<float, AgentSnap>(d2, s));
                }
                hits.Sort((l, r) => l.Key.CompareTo(r.Key));
                int i = 1;
                foreach (var h in hits)
                    arr[i++] = SnapTable(script, h.Value);
                return arr;
            }));

            Reg("player", (Func<DynValue, DynValue>)(n =>
            {
                int idx = n != null && n.Type == DataType.Number ? (int)n.Number : 1;
                var p = GameStateApi.FindPlayer(idx < 1 ? 1 : idx);
                return p == null ? DynValue.Nil : DynValue.NewTable(AgentTable(b, p));
            }));

            Reg("dist", (Func<double, double, double, double, double>)((x1, y1, x2, y2) =>
                Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2))));

            Reg("moveToward", (Func<double, double, bool>)((x, y) =>
            {
                var me = b.Agent;
                if (me == null || me.dead)
                    return false;
                me.SetFinalDestObject(null);
                me.SetFinalDestPosition(new Vector3((float)x, (float)y, 0f));
                return true;
            }));

            Reg("teleport", (Func<double, double, bool>)((x, y) =>
            {
                if (b.Agent == null)
                    return false;
                try { GameStateApi.Teleport(b.Uid, new Vector2((float)x, (float)y)); return true; }
                catch { return false; }
            }));

            Reg("say", (Func<string, bool>)(text =>
            {
                var me = b.Agent;
                if (me == null || string.IsNullOrEmpty(text))
                    return false;
                float now = Time.time;
                if (now - b.LastSay < SayCooldownSeconds)
                    return false; // engine-side rate limit — scripts WILL spam
                b.LastSay = now;
                try { me.Say(text, true); return true; }
                catch { return false; }
            }));

            Reg("hp", (Func<double, double, bool>)((uid, delta) =>
            {
                var me = b.Agent;
                var target = GameStateApi.FindAgent((int)uid);
                if (me == null || me.tr == null || target == null || target.tr == null)
                    return false;
                if (((Vector2)target.tr.position - (Vector2)me.tr.position).magnitude > MeleeRange)
                    return false; // melee-range helper, not artillery
                try { GameStateApi.ChangeHealth((int)uid, (float)delta); return true; }
                catch { return false; }
            }));

            Reg("attackNearest", (Func<DynValue, DynValue, DynValue>)((sub, dmg) =>
            {
                var me = b.Agent;
                if (me == null || me.tr == null)
                    return DynValue.Nil;
                string filter = sub != null && sub.Type == DataType.String ? sub.String : null;
                float damage = dmg != null && dmg.Type == DataType.Number ? (float)dmg.Number : 5f;
                Vector2 p = me.tr.position;
                int bestUid = 0;
                float bestD2 = MeleeRange * MeleeRange;
                foreach (var s in _snapshot)
                {
                    if (s.Uid == b.Uid || s.Dead)
                        continue;
                    if (filter != null
                        && s.Type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                        && s.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    float d2 = (s.X - p.x) * (s.X - p.x) + (s.Y - p.y) * (s.Y - p.y);
                    if (d2 <= bestD2)
                    {
                        bestD2 = d2;
                        bestUid = s.Uid;
                    }
                }
                if (bestUid == 0)
                    return DynValue.Nil;
                try { GameStateApi.ChangeHealth(bestUid, -Math.Abs(damage)); }
                catch { return DynValue.Nil; }
                return DynValue.NewNumber(bestUid);
            }));

            Reg("status", (Func<string, bool, bool>)((effect, on) =>
            {
                if (b.Agent == null)
                    return false;
                try { GameStateApi.SetStatus(b.Uid, effect, on); return true; } // SELF only
                catch { return false; }
            }));

            Reg("setGoal", (Func<string, DynValue, DynValue>)((goalName, target) =>
            {
                // SELF only — a behavior directs its own body, not others'.
                if (b.Agent == null)
                    return DynValue.Nil;
                int targetUid = target != null && target.Type == DataType.Number ? (int)target.Number : 0;
                try { return DynValue.NewString(GameStateApi.SetGoal(b.Uid, goalName, targetUid)); }
                catch (Exception e) { return DynValue.NewString("error: " + e.Message); }
            }));

            Reg("takeControl", (Func<bool, bool>)(on =>
            {
                // on=true: brain off, this behavior owns movement (moveToward
                // keeps working — PathfindingAI runs without the brain).
                if (b.Agent == null)
                    return false;
                try { GameStateApi.SetBrainActive(b.Uid, !on); return true; }
                catch { return false; }
            }));

            Reg("time", (Func<double>)(() => (double)Time.time));

            Reg("ignite", (Func<double, double, bool>)((x, y) =>
            {
                // Ground-mark telegraphs (boss strike shapes) — same path as
                // the ignite verb. Fire IS the "floor glows" tell.
                try { GameStateApi.Ignite(new Vector2((float)x, (float)y)); return true; }
                catch { return false; }
            }));

            Reg("label", (Func<string, bool>)(text =>
            {
                // SELF only — a behavior labels its own body (hp bar, ⚠ /
                // STAGGERED state flags). Empty/nil-ish text clears. Apply()
                // rebuilds the quest marker, so dedupe unchanged text here:
                // callers may refresh every tick, markers churn only on change.
                if (b.Agent == null)
                    return false;
                try
                {
                    var t = text ?? "";
                    if (t.Trim().Length == 0)
                    {
                        if (b.LastLabel != null) { Labels.Clear(b.Uid); b.LastLabel = null; }
                        return true;
                    }
                    if (t == b.LastLabel)
                        return true;
                    Labels.Apply(b.Uid, t);
                    b.LastLabel = t;
                    return true;
                }
                catch { return false; }
            }));

            b.ApiTable = DynValue.NewTable(api);
        }

        private static LuaTable AgentTable(Behavior b, Agent a)
        {
            LuaTable t = new LuaTable(b.Script);
            if (a == null || a.tr == null)
                return t;
            Vector2 p = a.tr.position;
            t["uid"] = a.UID;
            t["name"] = string.IsNullOrEmpty(a.agentRealName) ? a.agentName : a.agentRealName;
            t["type"] = a.agentName;
            t["x"] = p.x;
            t["y"] = p.y;
            t["hp"] = a.health;
            t["hpmax"] = a.healthMax;
            t["dead"] = a.dead;
            t["isPlayer"] = a.isPlayer != 0; // vanilla: int player number, 0 = NPC
            return t;
        }

        private static LuaTable SnapTable(Script script, AgentSnap s)
        {
            LuaTable t = new LuaTable(script);
            t["uid"] = s.Uid;
            t["name"] = s.Name;
            t["type"] = s.Type;
            t["x"] = s.X;
            t["y"] = s.Y;
            t["hp"] = s.Hp;
            t["hpmax"] = s.HpMax;
            t["dead"] = s.Dead;
            t["isPlayer"] = s.IsPlayer;
            return t;
        }
    }
}
