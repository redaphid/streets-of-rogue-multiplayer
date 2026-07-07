using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers.Tracing
{
    // Behavior-baseline tracer for the ECS migration (docs/ecs-netcode.md).
    // Game systems are Harmony-patched to emit one JSONL event per state
    // mutation; traces from vanilla runs become the reference that the ECS
    // replacements are diffed against.
    //
    // Enable with SOR_TRACE=1 (or [Tracing] Enabled in the config). Events are
    // buffered on a queue and written by a background thread, so patched
    // hot paths only pay for JObject construction.
    public static class Trace
    {
        private static readonly ConcurrentQueue<string> Queue = new ConcurrentQueue<string>();
        private static StreamWriter _writer;
        private static Thread _pump;
        private static volatile bool _running;
        private static int _dropped;

        public static bool Enabled { get; private set; }
        public static string Path { get; private set; }

        public static void Init()
        {
            var env = Environment.GetEnvironmentVariable("SOR_TRACE");
            Enabled = env == "1" || (env == null && EightPlayersPlugin.TraceEnabled.Value);
            if (!Enabled)
                return;

            var dir = Environment.GetEnvironmentVariable("SOR_TRACE_DIR")
                      ?? System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "traces");
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, $"trace-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{System.Diagnostics.Process.GetCurrentProcess().Id}.jsonl");
            _writer = new StreamWriter(Path, false, new UTF8Encoding(false)) { AutoFlush = false };
            _running = true;
            _pump = new Thread(Pump) { IsBackground = true, Name = "SorTracePump" };
            _pump.Start();

            Emit("trace", "start", new JObject
            {
                ["unity"] = Application.unityVersion,
                ["utc"] = DateTime.UtcNow.ToString("o"),
            });
            EightPlayersPlugin.Log.LogInfo($"TRACE writing to {Path}");
        }

        /// <summary>Queue one event. cat = system name (agent/inv/door/net/level), ev = verb.</summary>
        public static void Emit(string cat, string ev, JObject fields)
        {
            if (!Enabled)
                return;
            if (Queue.Count > 100_000)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            fields = fields ?? new JObject();
            fields["ts"] = Math.Round(UnityTime(), 3);
            fields["cat"] = cat;
            fields["ev"] = ev;
            Queue.Enqueue(fields.ToString(Newtonsoft.Json.Formatting.None));
        }

        // Time.unscaledTime is main-thread-only; cache it from the plugin's Update.
        private static float _lastTime;
        internal static void Tick() => _lastTime = Time.unscaledTime;
        private static float UnityTime() => _lastTime;

        private static void Pump()
        {
            while (_running || !Queue.IsEmpty)
            {
                bool wrote = false;
                while (Queue.TryDequeue(out var line))
                {
                    _writer.WriteLine(line);
                    wrote = true;
                }
                if (wrote)
                    _writer.Flush();
                Thread.Sleep(250);
            }
        }

        public static void Shutdown()
        {
            if (!Enabled)
                return;
            if (_dropped > 0)
                Emit("trace", "dropped", new JObject { ["count"] = _dropped });
            _running = false;
            try
            {
                _pump?.Join(2000);
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
            }
        }
    }
}
