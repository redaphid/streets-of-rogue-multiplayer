using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers.EcsNet
{
    // World objects (doors + destructible ObjectReals) as room entities.
    //
    // The NPC-authority client (lowest id) publishes one `wobj
    // {kind, type, x, y, lv}` entity per object after level load; the DO
    // persists them and fans them out (the publisher receives its own
    // spawns back like everyone else). Every client then RECONCILES:
    // match each wobj to its local twin by kind/type + nearest position and
    // keep entity<->object maps, so events can address world objects by
    // entity id — immune to the generation drift that position addressing
    // suffers from. v1 reconciles by matching only: missing/extra local
    // objects are counted and logged, not spawned/removed.
    internal sealed class WorldObjects
    {
        private readonly EcsWorld _world;
        private readonly EcsNetManager _mgr;

        private readonly Dictionary<int, PlayfieldObject> _byEntity = new Dictionary<int, PlayfieldObject>();
        private readonly Dictionary<PlayfieldObject, int> _byObject = new Dictionary<PlayfieldObject, int>();

        private int _publishedLv = int.MinValue;
        private int _reconciledCount = -1;
        private int _reconciledLv = int.MinValue;
        private float _nextTick;

        internal WorldObjects(EcsWorld world, EcsNetManager mgr)
        {
            _world = world;
            _mgr = mgr;
        }

        internal PlayfieldObject ObjectFor(int entity) =>
            _byEntity.TryGetValue(entity, out var o) && o != null ? o : null;

        internal int EntityFor(PlayfieldObject obj) =>
            obj != null && _byObject.TryGetValue(obj, out var e) ? e : -1;

        internal void OnLevelChanged()
        {
            _byEntity.Clear();
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

            // Reconcile whenever the visible wobj set changed (spawn burst
            // arriving, level advance, authority migration republished...).
            int seen = 0;
            _world.ForEach<WObj>((e, w) => { if (w.Lv == level) seen++; });
            if (seen > 0 && (seen != _reconciledCount || _reconciledLv != level))
            {
                _reconciledCount = seen;
                _reconciledLv = level;
                Reconcile(level);
            }
        }

        private void Publish(int level)
        {
            int n = 0;
            foreach (var door in GameStateApi.Doors())
            {
                if (door.tr == null) continue;
                Vector2 p = door.tr.position;
                _mgr.SendSpawnRaw(WobjComponents("door", door.doorType, p, level));
                n++;
            }
            foreach (var obj in GameStateApi.Objects())
            {
                if (obj is Door || obj.tr == null) continue;
                Vector2 p = obj.tr.position;
                _mgr.SendSpawnRaw(WobjComponents("obj", obj.objectName, p, level));
                n++;
            }
            EightPlayersPlugin.Log.LogInfo($"ECSNET wobj published: {n} world objects for level {level}");
        }

        private static JObject WobjComponents(string kind, string type, Vector2 p, int level) =>
            new JObject
            {
                ["wobj"] = new JObject
                {
                    ["kind"] = kind, ["type"] = type,
                    ["x"] = p.x, ["y"] = p.y, ["lv"] = level,
                }
            };

        private void Reconcile(int level)
        {
            _byEntity.Clear();
            _byObject.Clear();
            int matched = 0, missing = 0;
            _world.ForEach<WObj>((e, w) =>
            {
                if (w.Lv != level)
                    return;
                var pos = new Vector2(w.X, w.Y);
                PlayfieldObject local = w.Kind == "door"
                    ? (PlayfieldObject)GameStateApi.FindDoorAt(pos)
                    : GameStateApi.FindObjectAt(pos, w.Type);
                if (local == null)
                {
                    missing++;
                    return;
                }
                _byEntity[e] = local;
                _byObject[local] = e;
                matched++;
            });
            // Local objects the authority doesn't know about (drift, or
            // already-destroyed-on-authority). Counted for visibility.
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
