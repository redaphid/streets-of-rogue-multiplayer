using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers.EcsNet
{
    // The level's world-object layout (doors + destructible ObjectReals) as
    // ONE room entity: `wlayout {lv, objs: [{k, t, x, y}, ...]}` published by
    // the NPC-authority client after load. A single entity keeps the publish
    // to one message + one ack — an earlier per-object design (~300 spawns)
    // perturbed the game's own level loading — and gives the future JS client
    // the whole map in one read.
    //
    // Every client (the publisher included, via its own spawn echo) then
    // RECONCILES: match each layout record to its local twin by kind/type +
    // nearest position, keeping index<->object maps so events can address
    // world objects by layout INDEX — immune to the generation drift that
    // position addressing suffers from. v1 matches only: missing/extra local
    // objects are counted and logged, not spawned/removed.
    internal sealed class WorldObjects
    {
        private readonly EcsWorld _world;
        private readonly EcsNetManager _mgr;

        private readonly Dictionary<int, PlayfieldObject> _byIndex = new Dictionary<int, PlayfieldObject>();
        private readonly Dictionary<PlayfieldObject, int> _byObject = new Dictionary<PlayfieldObject, int>();

        private int _publishedLv = int.MinValue;
        private int _reconciledLv = int.MinValue;
        private int _reconciledCount = -1;
        private float _nextTick;

        internal WorldObjects(EcsWorld world, EcsNetManager mgr)
        {
            _world = world;
            _mgr = mgr;
        }

        internal PlayfieldObject ObjectAt(int index) =>
            _byIndex.TryGetValue(index, out var o) && o != null ? o : null;

        internal int IndexFor(PlayfieldObject obj) =>
            obj != null && _byObject.TryGetValue(obj, out var i) ? i : -1;

        internal void OnLevelChanged()
        {
            _byIndex.Clear();
            _byObject.Clear();
            _reconciledCount = -1;
        }

        /// <summary>Called each publish tick when welcomed && world stable.</summary>
        internal void Tick(bool authority, int level)
        {
            if (Time.unscaledTime < _nextTick)
                return;
            _nextTick = Time.unscaledTime + 5f;

            if (authority && _publishedLv != level)
            {
                _publishedLv = level;
                Publish(level);
            }

            // Reconcile when a layout for my level is visible and changed.
            var objs = FindLayout(level, out _);
            if (objs != null && (objs.Count != _reconciledCount || _reconciledLv != level))
            {
                _reconciledLv = level;
                _reconciledCount = objs.Count;
                Reconcile(level, objs);
            }
        }

        /// <summary>The layout array for a level from any entity's raw JSON.</summary>
        private JArray FindLayout(int level, out int entity)
        {
            foreach (var kv in _world.Raw)
            {
                if (kv.Value["wlayout"] is JObject w && ((int?)w["lv"] ?? -1) == level
                    && w["objs"] is JArray objs)
                {
                    entity = kv.Key;
                    return objs;
                }
            }
            entity = -1;
            return null;
        }

        private void Publish(int level)
        {
            // Retire other levels' layout entities we own (a departed
            // authority's were auto-despawned with its connection).
            foreach (var kv in new List<KeyValuePair<int, JObject>>(_world.Raw))
                if (kv.Value["wlayout"] is JObject w && ((int?)w["lv"] ?? -1) != level)
                    _mgr.SendDespawn(kv.Key);

            var objs = new JArray();
            foreach (var door in GameStateApi.Doors())
            {
                if (door.tr == null) continue;
                Vector2 p = door.tr.position;
                objs.Add(new JObject { ["k"] = "door", ["t"] = door.doorType, ["x"] = p.x, ["y"] = p.y });
            }
            foreach (var obj in GameStateApi.Objects())
            {
                if (obj is Door || obj.tr == null) continue;
                Vector2 p = obj.tr.position;
                objs.Add(new JObject { ["k"] = "obj", ["t"] = obj.objectName, ["x"] = p.x, ["y"] = p.y });
            }
            _mgr.SendSpawnRaw(new JObject
            {
                ["wlayout"] = new JObject { ["lv"] = level, ["objs"] = objs }
            });
            EightPlayersPlugin.Log.LogInfo($"ECSNET wobj published: {objs.Count} world objects for level {level} (one layout entity)");
        }

        private void Reconcile(int level, JArray objs)
        {
            _byIndex.Clear();
            _byObject.Clear();
            int matched = 0, missing = 0;
            for (int i = 0; i < objs.Count; i++)
            {
                if (!(objs[i] is JObject rec))
                    continue;
                var pos = new Vector2((float?)rec["x"] ?? 0, (float?)rec["y"] ?? 0);
                PlayfieldObject local = (string)rec["k"] == "door"
                    ? (PlayfieldObject)GameStateApi.FindDoorAt(pos)
                    : GameStateApi.FindObjectAt(pos, (string)rec["t"]);
                if (local == null)
                {
                    missing++;
                    continue;
                }
                _byIndex[i] = local;
                _byObject[local] = i;
                matched++;
            }
            int extra = 0;
            foreach (var door in GameStateApi.Doors())
                if (door.tr != null && !_byObject.ContainsKey(door)) extra++;
            foreach (var obj in GameStateApi.Objects())
                if (!(obj is Door) && obj.tr != null && !_byObject.ContainsKey(obj)) extra++;
            EightPlayersPlugin.Log.LogInfo(
                $"ECSNET wobj reconcile: matched={matched} missingLocal={missing} extraLocal={extra} (level {level})");
        }
    }
}
