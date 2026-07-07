using Newtonsoft.Json.Linq;

namespace EightPlayers.EcsNet
{
    // C# twin of worker/src/protocol.ts — keep the two in sync.
    // Outgoing messages are built as JObjects; incoming frames are parsed into
    // a single ServerMsg record with only the fields that message type uses.

    public static class Protocol
    {
        public const int Version = 1;

        public static string Hello(string name) =>
            new JObject { ["t"] = "hello", ["proto"] = Version, ["name"] = name }.ToString(Newtonsoft.Json.Formatting.None);

        public static string Spawn(int tmp, JObject components) =>
            new JObject { ["t"] = "spawn", ["tmp"] = tmp, ["components"] = components }.ToString(Newtonsoft.Json.Formatting.None);

        public static string Set(int entity, JObject components) =>
            new JObject { ["t"] = "set", ["e"] = entity, ["components"] = components }.ToString(Newtonsoft.Json.Formatting.None);

        public static string Despawn(int entity) =>
            new JObject { ["t"] = "despawn", ["e"] = entity }.ToString(Newtonsoft.Json.Formatting.None);

        public static string Ping(long ts) =>
            new JObject { ["t"] = "ping", ["ts"] = ts }.ToString(Newtonsoft.Json.Formatting.None);

        public static string World(string seed) =>
            new JObject { ["t"] = "world", ["seed"] = seed }.ToString(Newtonsoft.Json.Formatting.None);

        public static string WorldNum(int num) =>
            new JObject { ["t"] = "world", ["num"] = num }.ToString(Newtonsoft.Json.Formatting.None);

        public static string Event(string kind, JObject data) =>
            new JObject { ["t"] = "event", ["kind"] = kind, ["data"] = data }.ToString(Newtonsoft.Json.Formatting.None);

        public static JObject PosComponent(float x, float y) =>
            new JObject { ["pos"] = new JObject { ["x"] = x, ["y"] = y } };

        public static JObject HpComponent(float cur, float max) =>
            new JObject { ["hp"] = new JObject { ["cur"] = cur, ["max"] = max } };

        public static JObject LevelComponent(int seed, int num) =>
            new JObject { ["level"] = new JObject { ["seed"] = seed, ["num"] = num } };

        public static JObject NpcComponents(int index, string type, float x, float y) =>
            new JObject
            {
                ["npc"] = new JObject { ["i"] = index, ["type"] = type },
                ["pos"] = new JObject { ["x"] = x, ["y"] = y },
            };

        public static JObject PlayerComponents(string name, int color, string charType, float x, float y, float hp, float hpMax) =>
            new JObject
            {
                ["player"] = new JObject { ["name"] = name, ["color"] = color, ["char"] = charType },
                ["pos"] = new JObject { ["x"] = x, ["y"] = y },
                ["hp"] = new JObject { ["cur"] = hp, ["max"] = hpMax },
            };
    }

    public sealed class ServerMsg
    {
        public string T;
        public int You;           // welcome
        public string Room;       // welcome
        public JArray Snapshot;   // welcome
        public JArray Peers;      // welcome
        public int Entity;        // spawn/set/despawn
        public int Owner;         // spawn
        public int Tmp = -1;      // spawn (only on the owner's copy)
        public JObject Components; // spawn/set
        public int PeerId;        // peer
        public string PeerName;   // peer
        public bool Joined;       // peer
        public long Ts;           // pong
        public string Message;    // error
        public string WorldSeed;  // welcome (null if unset) / world
        public int WorldLevel = 1; // welcome / world
        public string Kind;       // event
        public JObject EventData; // event
        public int From;          // event

        public static ServerMsg Parse(string raw)
        {
            var jo = JObject.Parse(raw);
            var msg = new ServerMsg { T = (string)jo["t"] };
            switch (msg.T)
            {
                case "welcome":
                    msg.You = (int)jo["you"];
                    msg.Room = (string)jo["room"];
                    msg.Snapshot = (JArray)jo["snapshot"];
                    msg.Peers = (JArray)jo["peers"];
                    if (jo["world"] is JObject world)
                    {
                        msg.WorldSeed = (string)world["seed"];
                        msg.WorldLevel = (int?)world["num"] ?? 1;
                    }
                    break;
                case "world":
                    msg.WorldSeed = (string)jo["seed"];
                    msg.WorldLevel = (int?)jo["num"] ?? 1;
                    break;
                case "event":
                    msg.Kind = (string)jo["kind"];
                    msg.EventData = jo["data"] as JObject;
                    msg.From = (int?)jo["from"] ?? 0;
                    break;
                case "spawn":
                    msg.Entity = (int)jo["e"];
                    msg.Owner = (int)jo["owner"];
                    msg.Components = (JObject)jo["components"];
                    if (jo["tmp"] != null) msg.Tmp = (int)jo["tmp"];
                    break;
                case "set":
                    msg.Entity = (int)jo["e"];
                    msg.Components = (JObject)jo["components"];
                    break;
                case "despawn":
                    msg.Entity = (int)jo["e"];
                    break;
                case "peer":
                    msg.PeerId = (int)jo["id"];
                    msg.PeerName = (string)jo["name"];
                    msg.Joined = (bool)jo["joined"];
                    break;
                case "pong":
                    msg.Ts = (long)jo["ts"];
                    break;
                case "error":
                    msg.Message = (string)jo["message"];
                    break;
            }
            return msg;
        }
    }
}
