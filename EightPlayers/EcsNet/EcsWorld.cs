using System;
using System.Collections.Generic;

namespace EightPlayers.EcsNet
{
    // Minimal data-oriented ECS mirror of the room state held by the Durable
    // Object. Entity ids are the server-assigned integers; components are
    // plain structs in per-type stores.

    public struct Pos
    {
        public float X;
        public float Y;
    }

    public struct PlayerInfo
    {
        public string Name;
        public int Color;
        /// <summary>Game agent type (e.g. "Thief") used to spawn a matching avatar.</summary>
        public string Char;
    }

    public struct Owned
    {
        public int ClientId;
    }

    public struct Hp
    {
        public float Cur;
        public float Max;
    }

    public struct LevelId
    {
        public int Seed;
        public int Num;
        /// <summary>Door-geometry fingerprint; 0 = unknown/not loaded.</summary>
        public uint Hash;
    }

    /// <summary>A world object (door/destructible) published by the level
    /// authority; the local twin is resolved by WorldObjects reconciliation.</summary>
    public struct WObj
    {
        public string Kind;
        public string Type;
        public float X;
        public float Y;
        public int Lv;
    }

    /// <summary>The player's currently equipped weapon (shown on avatars).</summary>
    public struct WeaponInfo
    {
        public string Name;
    }

    /// <summary>Marks an entity as a mirrored NPC; Index is its level spawn order.</summary>
    public struct NpcTag
    {
        public int Index;
        public string Type;
    }

    public struct DeadTag
    {
        public bool Value;
    }

    public sealed class EcsWorld
    {
        private interface IStore
        {
            void Remove(int e);
        }

        private sealed class Store<T> : IStore
        {
            public readonly Dictionary<int, T> Map = new Dictionary<int, T>();
            public void Remove(int e) => Map.Remove(e);
        }

        private readonly HashSet<int> _entities = new HashSet<int>();
        private readonly Dictionary<Type, IStore> _stores = new Dictionary<Type, IStore>();

        // Verbatim JSON of every component as last received, merged per
        // entity — the debug harness's full-fidelity view (typed stores only
        // keep the keys this build knows about).
        public readonly Dictionary<int, Newtonsoft.Json.Linq.JObject> Raw =
            new Dictionary<int, Newtonsoft.Json.Linq.JObject>();

        public IEnumerable<int> Entities => _entities;
        public int Count => _entities.Count;

        public void Spawn(int e)
        {
            _entities.Add(e);
        }

        public void Despawn(int e)
        {
            _entities.Remove(e);
            _Raw_Remove(e);
            foreach (var store in _stores.Values)
                store.Remove(e);
        }

        public void Clear()
        {
            _entities.Clear();
            Raw.Clear();
            _stores.Clear();
        }

        // Null component values must WIN the merge (writing {"input":null}
        // clears an intent); Json.NET ignores nulls by default.
        private static readonly Newtonsoft.Json.Linq.JsonMergeSettings RawMerge =
            new Newtonsoft.Json.Linq.JsonMergeSettings
            {
                MergeNullValueHandling = Newtonsoft.Json.Linq.MergeNullValueHandling.Merge,
            };

        public void MergeRaw(int e, Newtonsoft.Json.Linq.JObject components)
        {
            if (components == null)
                return;
            _entities.Add(e);
            if (Raw.TryGetValue(e, out var existing))
                existing.Merge(components, RawMerge);
            else
                Raw[e] = (Newtonsoft.Json.Linq.JObject)components.DeepClone();
        }

        private void _Raw_Remove(int e) => Raw.Remove(e);

        public bool Exists(int e) => _entities.Contains(e);

        public void Set<T>(int e, T value) where T : struct
        {
            _entities.Add(e);
            GetStore<T>().Map[e] = value;
        }

        public bool TryGet<T>(int e, out T value) where T : struct
        {
            return GetStore<T>().Map.TryGetValue(e, out value);
        }

        public void ForEach<T>(Action<int, T> visit) where T : struct
        {
            foreach (var kv in GetStore<T>().Map)
                visit(kv.Key, kv.Value);
        }

        private Store<T> GetStore<T>()
        {
            if (!_stores.TryGetValue(typeof(T), out var store))
            {
                store = new Store<T>();
                _stores[typeof(T)] = store;
            }
            return (Store<T>)store;
        }
    }
}
