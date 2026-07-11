using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers
{
    // Streaming-HTTP command channel — the fast path over the file-polling
    // channel (which stays active as fallback / backwards compat).
    //
    //   POST /cmd     body = raw verb line(s)  ->  200 + the exact reply text
    //                 the file channel would append to ep_out.txt (incl. the
    //                 "> cmd" echo). Verb errors are formatted the same way
    //                 the file channel formats them ("error: ..."), still 200.
    //                 5xx is reserved for channel-level failures.
    //   GET  /events  long-lived chunked response streaming newline-delimited
    //                 JSON event frames: {"event":"level_loaded"|"agent_died"|
    //                 "player_hp", ...}. Plain NDJSON over chunked transfer —
    //                 streaming HTTP, deliberately NOT SSE.
    //   GET  /state   the "state" summary (curl-able nicety).
    //
    // Threading: HttpListener accepts + reads on background threads; verb
    // EXECUTION always hops to the Unity main thread via a queue drained by
    // Tick() (the exact same code path the file channel uses), and the request
    // thread blocks until its verb completes. Multiple in-flight requests are
    // fine — HTTP gives per-request correlation for free.
    //
    // Port: EP_HTTP_PORT env or 7801; if busy (several game instances on one
    // machine) the next ports are probed. The ACTUAL bound port is written to
    // <BepInEx>/ep_port.txt so clients discover it.
    internal static class HttpChannel
    {
        private const int DefaultPort = 7801;
        private const int PortProbeRange = 20;
        private const float EventTickInterval = 0.25f;
        private const float PlayerHpThreshold = 3f;

        private static HttpListener _listener;
        private static int _port;
        private static string PortPath => Path.Combine(BepInEx.Paths.BepInExRootPath, "ep_port.txt");

        // ---- main-thread execution queue -----------------------------------
        private sealed class PendingCmd
        {
            public string Text;
            public string Result;
            public Exception Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly ConcurrentQueue<PendingCmd> _queue = new ConcurrentQueue<PendingCmd>();

        // ---- event stream subscribers ---------------------------------------
        private sealed class EventClient
        {
            public HttpListenerResponse Response;
            public Stream Stream;
            public readonly object WriteLock = new object();
        }

        private static readonly List<EventClient> _eventClients = new List<EventClient>();

        internal static void Start()
        {
            try
            {
                int wanted = DefaultPort;
                var env = Environment.GetEnvironmentVariable("EP_HTTP_PORT");
                if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var p) && p > 0)
                    wanted = p;
                for (int i = 0; i < PortProbeRange; i++)
                {
                    try
                    {
                        var listener = new HttpListener();
                        listener.Prefixes.Add($"http://127.0.0.1:{wanted + i}/");
                        listener.Start();
                        _listener = listener;
                        _port = wanted + i;
                        break;
                    }
                    catch (Exception) { /* port busy (another instance) — probe on */ }
                }
                if (_listener == null)
                {
                    EightPlayersPlugin.Log.LogError($"EPHTTP could not bind any port in {wanted}..{wanted + PortProbeRange - 1}; HTTP channel disabled (file channel still works)");
                    return;
                }
                File.WriteAllText(PortPath, _port + "\n");
                var t = new Thread(AcceptLoop) { IsBackground = true, Name = "EPHTTP-accept" };
                t.Start();
                EightPlayersPlugin.Log.LogInfo($"EPHTTP command channel listening on http://127.0.0.1:{_port}/ (port file: {PortPath})");
            }
            catch (Exception e)
            {
                EightPlayersPlugin.Log.LogError($"EPHTTP failed to start: {e.GetType().Name}: {e.Message} (file channel still works)");
            }
        }

        internal static void Shutdown()
        {
            try { _listener?.Close(); } catch { }
            _listener = null;
            lock (_eventClients)
            {
                foreach (var c in _eventClients)
                    try { c.Response.Close(); } catch { }
                _eventClients.Clear();
            }
            try { if (File.Exists(PortPath)) File.Delete(PortPath); } catch { }
        }

        // ---- background: accept + per-request handling ----------------------
        private static void AcceptLoop()
        {
            var listener = _listener;
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; } // listener closed
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/cmd")
                    HandleCmd(ctx);
                else if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/events")
                    HandleEvents(ctx);
                else if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/state")
                    RunOnMainThread(ctx, "state");
                else
                    Respond(ctx.Response, 404, "unknown endpoint (POST /cmd, GET /events, GET /state)");
            }
            catch (Exception e)
            {
                try { Respond(ctx.Response, 500, $"channel error: {e.GetType().Name}: {e.Message}"); } catch { }
            }
        }

        private static void HandleCmd(HttpListenerContext ctx)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();
            if (string.IsNullOrEmpty(body) || body.Trim().Length == 0)
            {
                Respond(ctx.Response, 400, "empty body — POST the verb line(s) as text/plain");
                return;
            }
            RunOnMainThread(ctx, body);
        }

        /// <summary>Enqueue the verb text for the Unity main thread (drained by
        /// Tick — the same execution path as the file channel) and block this
        /// request thread until it completes, then write the reply.</summary>
        private static void RunOnMainThread(HttpListenerContext ctx, string text)
        {
            var pending = new PendingCmd { Text = text };
            _queue.Enqueue(pending);
            // Generous wait: some verbs legitimately take a while, and during a
            // level load the main thread may not tick for a few seconds.
            if (!pending.Done.Wait(TimeSpan.FromSeconds(30)))
            {
                Respond(ctx.Response, 503, "main thread did not drain the command queue (game loading or wedged) — retry, or use the file channel");
                return;
            }
            if (pending.Error != null)
                Respond(ctx.Response, 500, $"channel error: {pending.Error.GetType().Name}: {pending.Error.Message}");
            else
                Respond(ctx.Response, 200, pending.Result ?? "");
        }

        private static void HandleEvents(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            res.StatusCode = 200;
            res.ContentType = "application/x-ndjson";
            res.SendChunked = true;
            var client = new EventClient { Response = res, Stream = res.OutputStream };
            // Flush headers + a hello frame immediately so the subscriber knows
            // the stream is live before the first real event.
            var hello = new JObject { ["event"] = "hello", ["port"] = _port };
            WriteFrame(client, hello.ToString(Newtonsoft.Json.Formatting.None));
            lock (_eventClients)
                _eventClients.Add(client);
            // Handler thread returns; the response stays open and is written to
            // by Broadcast() until the client disconnects.
        }

        private static void Respond(HttpListenerResponse res, int status, string text)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text ?? "");
                res.StatusCode = status;
                res.ContentType = "text/plain; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Close();
            }
            catch { /* client went away */ }
        }

        // ---- main thread: drain verbs + detect/push events ------------------
        private static float _nextEventTick;
        private static bool _eventsPrimed;
        private static bool _prevLoaded;
        private static int _prevSeed, _prevLevel;
        private static readonly Dictionary<int, bool> _agentDead = new Dictionary<int, bool>();
        private static readonly Dictionary<int, float> _playerHp = new Dictionary<int, float>();

        internal static void Tick()
        {
            // 1. Execute HTTP-submitted verbs on the main thread, exactly like
            //    the file channel would.
            while (_queue.TryDequeue(out var pending))
            {
                try { pending.Result = CommandChannel.ExecuteCaptured(pending.Text); }
                catch (Exception e) { pending.Error = e; }
                pending.Done.Set();
            }

            // 2. Cheap event detection, only while someone is subscribed.
            lock (_eventClients)
            {
                if (_eventClients.Count == 0)
                {
                    _eventsPrimed = false; // re-prime baselines on next subscribe
                    return;
                }
            }
            if (Time.unscaledTime < _nextEventTick)
                return;
            _nextEventTick = Time.unscaledTime + EventTickInterval;
            try { DetectEvents(); }
            catch { /* never let event plumbing break the game loop */ }
        }

        private static void DetectEvents()
        {
            var gc = GameController.gameController;
            bool loaded = gc != null && gc.loadLevel != null && gc.sessionDataBig != null && gc.loadCompleteReally;
            int seed = loaded ? gc.loadLevel.randomSeedNum : 0;
            int level = loaded ? gc.sessionDataBig.curLevel : 0;

            if (!_eventsPrimed)
            {
                // First tick with a subscriber: prime baselines WITHOUT emitting
                // (don't replay deaths that happened while nobody listened).
                _prevLoaded = loaded;
                _prevSeed = seed;
                _prevLevel = level;
                _agentDead.Clear();
                _playerHp.Clear();
                if (loaded)
                    PrimeAgents(gc);
                _eventsPrimed = true;
                return;
            }

            // level_loaded: load-complete transition, or a new (seed, level).
            if (loaded && (!_prevLoaded || seed != _prevSeed || level != _prevLevel))
            {
                Broadcast(new JObject
                {
                    ["event"] = "level_loaded",
                    ["level"] = level,
                    ["seed"] = seed,
                });
                _agentDead.Clear(); // uids churn per level
                _playerHp.Clear();
                if (loaded)
                    PrimeAgents(gc);
            }
            _prevLoaded = loaded;
            _prevSeed = seed;
            _prevLevel = level;
            if (!loaded)
                return;

            // agent_died: alive -> dead transitions (diff-based).
            foreach (var a in gc.agentList)
            {
                if (a == null)
                    continue;
                _agentDead.TryGetValue(a.UID, out var wasDead); // false if new
                if (a.dead && !wasDead && _agentDead.ContainsKey(a.UID))
                {
                    Broadcast(new JObject
                    {
                        ["event"] = "agent_died",
                        ["uid"] = a.UID,
                        ["name"] = string.IsNullOrEmpty(a.agentRealName) ? a.agentName : a.agentRealName,
                        ["type"] = a.agentName,
                        ["isPlayer"] = a.isPlayer,
                    });
                }
                _agentDead[a.UID] = a.dead;
            }

            // player_hp: per-player health swings past the threshold.
            int num = 0;
            foreach (var a in gc.playerAgentList)
            {
                num++;
                if (a == null)
                    continue;
                if (_playerHp.TryGetValue(a.UID, out var prev))
                {
                    float delta = a.health - prev;
                    if (Mathf.Abs(delta) >= PlayerHpThreshold)
                    {
                        Broadcast(new JObject
                        {
                            ["event"] = "player_hp",
                            ["uid"] = a.UID,
                            ["player"] = num,
                            ["hp"] = Mathf.Round(a.health * 10f) / 10f,
                            ["hpMax"] = Mathf.Round(a.healthMax * 10f) / 10f,
                            ["delta"] = Mathf.Round(delta * 10f) / 10f,
                        });
                        _playerHp[a.UID] = a.health;
                    }
                }
                else
                {
                    _playerHp[a.UID] = a.health;
                }
            }
        }

        private static void PrimeAgents(GameController gc)
        {
            foreach (var a in gc.agentList)
                if (a != null)
                    _agentDead[a.UID] = a.dead;
            foreach (var a in gc.playerAgentList)
                if (a != null)
                    _playerHp[a.UID] = a.health;
        }

        /// <summary>Push one NDJSON event frame to every /events subscriber.
        /// Internal so other features (e.g. DialogueMenu's menu_choice) can
        /// emit on the same stream. Call from the main thread.</summary>
        internal static void Broadcast(JObject frame)
        {
            var line = frame.ToString(Newtonsoft.Json.Formatting.None);
            EventClient[] clients;
            lock (_eventClients)
                clients = _eventClients.ToArray();
            foreach (var c in clients)
                WriteFrame(c, line);
        }

        private static void WriteFrame(EventClient c, string line)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                lock (c.WriteLock)
                {
                    c.Stream.Write(bytes, 0, bytes.Length);
                    c.Stream.Flush();
                }
            }
            catch
            {
                lock (_eventClients)
                    _eventClients.Remove(c);
                try { c.Response.Close(); } catch { }
            }
        }
    }
}
