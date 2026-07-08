using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers.EcsNet
{
    // Phase-0 bridge between the running game and a GameRoom Durable Object.
    // Publishes every local player agent as an entity (player + pos components)
    // and renders every remote player entity as a named ghost marker in the
    // world. No Mirror involvement at all — this is the seed of the
    // ECS-replaces-Mirror netcode, see docs/ecs-netcode.md.
    public sealed class EcsNetManager : MonoBehaviour
    {
        private const float ReconnectDelay = 5f;
        private const float MinMove = 0.01f;

        private readonly EcsWorld _world = new EcsWorld();
        private readonly List<LocalPlayer> _locals = new List<LocalPlayer>();
        private readonly Dictionary<int, Ghost> _ghosts = new Dictionary<int, Ghost>();
        private readonly RemoteAvatars _avatars = new RemoteAvatars();
        private readonly NpcSync _npcs = new NpcSync();
        private WorldObjects _wobjs;
        private readonly HashSet<int> _peerIds = new HashSet<int>();
        private bool _wasNpcAuthority;

        /// <summary>Lowest client id in the room simulates the NPCs.</summary>
        public bool IsNpcAuthority
        {
            get
            {
                if (!_welcomed)
                    return false;
                foreach (var id in _peerIds)
                    if (id < _myClientId)
                        return false;
                return true;
            }
        }

        private NetClient _client;
        private NetState _prevState = NetState.Disconnected;
        private int _myClientId = -1;
        private bool _welcomed;
        private float _nextConnectAt;
        private float _nextSendAt;
        private int _nextTmp = 1;
        private Sprite _ghostSprite;

        public static EcsNetManager Instance { get; private set; }

        /// <summary>
        /// Room-wide map seed adopted from the Durable Object. Read by
        /// ForceSeed_Patch at level load so every game in the room generates
        /// the same world. Null until the room's seed is known.
        /// </summary>
        public static string AdoptedSeed { get; private set; }

        /// <summary>The party's current level per the room (monotonic).</summary>
        public static int RoomLevel { get; private set; } = 1;

        private bool _worldClaimSent;
        private float _nextFollowAt;

        private sealed class LocalPlayer
        {
            public Agent Agent;
            public int Entity = -1;
            public int Tmp = -1;
            public Vector2 LastSent = new Vector2(float.NaN, float.NaN);
            public bool HpDirty;
            public LevelId SentLevel;
            public string SentChar;
            public float NextPosKeepalive;
        }

        // pos is volatile in the Durable Object (never persisted), so a DO
        // restart drops it and a stationary player would never repair it.
        // Resend pos periodically even without movement.
        private const float PosKeepaliveSeconds = 5f;

        private void Awake()
        {
            Instance = this;
        }

        /// <summary>Called from the ChangeHealth choke-point hook (EcsHooks).</summary>
        public void OnLocalHealthChanged(Agent agent, float delta = 0f)
        {
            if (agent == null)
                return;
            foreach (var lp in _locals)
                if (ReferenceEquals(lp.Agent, agent))
                {
                    lp.HpDirty = true;
                    return;
                }
            // A hit on someone's avatar is a hit on THAT player: relay it so
            // the owner applies authoritative damage to their real character.
            // The local avatar's hp change is cosmetic; the owner's hp
            // component update overwrites it.
            if (_welcomed && delta != 0f && _avatars.TryGetEntityFor(agent, out var entity))
            {
                _client.Send(Protocol.Event("pvp-hit", new JObject { ["e"] = entity, ["dmg"] = delta }));
                return;
            }
            if (_wasNpcAuthority)
                _npcs.MarkNpcHealth(agent);
        }

        /// <summary>Called from the Add/RemoveStatusEffect hooks (EcsHooks).</summary>
        public void OnLocalStatusChanged(Agent agent, string effect, bool on)
        {
            if (agent == null || string.IsNullOrEmpty(effect) || !_welcomed)
                return;
            // AddStatusEffect can refuse (preventStatusEffects, dead agent,
            // Dizzy conflicts...) and the hook is a postfix — only publish
            // "on" when the effect actually landed.
            if (on && !agent.statusEffects.hasStatusEffect(effect))
                return;
            foreach (var lp in _locals)
                if (ReferenceEquals(lp.Agent, agent) && lp.Entity >= 0)
                {
                    _client.Send(Protocol.Event("status",
                        new JObject { ["e"] = lp.Entity, ["name"] = effect, ["on"] = on }));
                    return;
                }
        }

        /// <summary>Called from the EquipWeapon choke-point hook (EcsHooks).</summary>
        public void OnLocalWeaponEquipped(Agent agent, string itemName)
        {
            if (agent == null || string.IsNullOrEmpty(itemName) || !_welcomed)
                return;
            foreach (var lp in _locals)
                if (ReferenceEquals(lp.Agent, agent) && lp.Entity >= 0)
                {
                    _client.Send(Protocol.Set(lp.Entity,
                        new JObject { ["weapon"] = new JObject { ["name"] = itemName } }));
                    return;
                }
        }

        /// <summary>Called from the SetupDeath choke-point hook (EcsHooks).</summary>
        public void OnAgentDeath(Agent agent)
        {
            if (agent == null)
                return;
            // My own player died: mark it on my entity so peers' avatars die too.
            foreach (var lp in _locals)
                if (ReferenceEquals(lp.Agent, agent))
                {
                    if (_welcomed && lp.Entity >= 0)
                        SendDead(lp.Entity, agent.health, agent.healthMax);
                    return;
                }
            if (_wasNpcAuthority)
                _npcs.MarkNpcDeath(agent);
        }

        private sealed class Ghost
        {
            public GameObject Root;
            public TextMesh Label;
            public Vector2 Target;
        }

        private static string ServerUrl =>
            Environment.GetEnvironmentVariable("SOR_ECS_SERVER") ?? EightPlayersPlugin.EcsServerUrl.Value;

        private static string RoomCode =>
            (Environment.GetEnvironmentVariable("SOR_ECS_ROOM") ?? EightPlayersPlugin.EcsRoom.Value).Trim().ToUpperInvariant();

        private static string PlayerName =>
            Environment.GetEnvironmentVariable("SOR_ECS_NAME") ?? EightPlayersPlugin.EcsPlayerName.Value;

        private static bool Enabled => RoomCode.Length > 0;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                _showRoomUi = !_showRoomUi;
                if (_showRoomUi)
                {
                    _roomUiCode = RoomCode;
                    _roomUiName = PlayerName;
                }
            }
            if (!Enabled)
            {
                if (_client != null) TearDown();
                return;
            }

            if (_client == null)
                _client = new NetClient();

            PumpConnection();
            if (_client.State != NetState.Connected)
                return;

            while (_client.TryReceive(out var frame))
                Apply(ServerMsg.Parse(frame));

            if (_welcomed && Time.unscaledTime >= _nextSendAt)
            {
                _nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1, EightPlayersPlugin.EcsSendHz.Value);
                PublishLocalPlayers();
            }

            // The loader owns the world during level loads: spawning or
            // despawning avatar agents mid-load kills the game's own load
            // coroutine (WaitForRealStart died in AwakenObjects with a
            // half-bound StatusEffectDisplay). ECS state keeps updating;
            // game mutations wait for WorldStable.
            if (EightPlayersPlugin.EcsRealAvatars.Value && WorldStable)
            {
                _avatars.Sync(_world, _myClientId, LocalLevel());
                _avatars.Drive();
            }
            else
            {
                UpdateGhosts();
            }

            if (EightPlayersPlugin.EcsNpcSync.Value && _welcomed)
            {
                var authority = IsNpcAuthority;
                if (_wasNpcAuthority && !authority)
                    _npcs.RetractPublished(this);
                if (!_wasNpcAuthority && authority)
                    _npcs.UnbindAll(); // we simulate now; give brains back
                _wasNpcAuthority = authority;

                if (authority)
                {
                    _npcs.PublishTick(this);
                }
                else
                {
                    _npcs.FollowerSync(_world, _myClientId);
                    _npcs.Drive();
                }
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            TearDown();
        }

        private void TearDown()
        {
            _client?.Dispose();
            _client = null;
            ResetSession();
        }

        private void ResetSession()
        {
            _welcomed = false;
            _myClientId = -1;
            _worldClaimSent = false;
            AdoptedSeed = null;
            RoomLevel = 1;
            _peerIds.Clear();
            _wasNpcAuthority = false;
            _npcs.Reset();
            _world.Clear();
            _avatars.Clear();
            foreach (var ghost in _ghosts.Values)
                if (ghost.Root != null) Destroy(ghost.Root);
            _ghosts.Clear();
            foreach (var lp in _locals)
            {
                lp.Entity = -1;
                lp.Tmp = -1;
                lp.LastSent = new Vector2(float.NaN, float.NaN);
            }
        }

        private void PumpConnection()
        {
            var state = _client.State;
            if (state == NetState.Disconnected && Time.unscaledTime >= _nextConnectAt)
            {
                _nextConnectAt = Time.unscaledTime + ReconnectDelay;
                var url = $"{ServerUrl.TrimEnd('/')}/room/{RoomCode}/ws";
                EightPlayersPlugin.Log.LogInfo($"ECSNET connecting to {url}");
                _client.Connect(url);
            }
            else if (state == NetState.Connected && _prevState != NetState.Connected)
            {
                _client.Send(Protocol.Hello(PlayerName));
            }
            else if (state == NetState.Disconnected && _prevState != NetState.Disconnected)
            {
                EightPlayersPlugin.Log.LogWarning($"ECSNET disconnected ({_client.LastError})");
                ResetSession();
            }
            _prevState = state;
        }

        // ---- incoming ----

        private void Apply(ServerMsg msg)
        {
            switch (msg.T)
            {
                case "welcome":
                    _myClientId = msg.You;
                    _welcomed = true;
                    _world.Clear();
                    _peerIds.Clear();
                    foreach (var peer in msg.Peers)
                        _peerIds.Add((int)peer["id"]);
                    foreach (var rec in msg.Snapshot)
                    {
                        var e = (int)rec["e"];
                        _world.Spawn(e);
                        _world.Set(e, new Owned { ClientId = (int)rec["owner"] });
                        ApplyComponents(e, (JObject)rec["components"]);
                    }
                    if (!string.IsNullOrEmpty(msg.WorldSeed))
                    {
                        AdoptedSeed = msg.WorldSeed;
                        RoomLevel = msg.WorldLevel;
                        EightPlayersPlugin.Log.LogInfo($"ECSNET room world seed: {msg.WorldSeed} level {msg.WorldLevel}");
                    }
                    EightPlayersPlugin.Log.LogInfo(
                        $"ECSNET joined room {msg.Room} as client {msg.You} ({msg.Snapshot.Count} entities, {msg.Peers.Count} peers)");
                    break;

                case "world":
                    AdoptedSeed = msg.WorldSeed;
                    if (msg.WorldLevel > RoomLevel)
                        RoomLevel = msg.WorldLevel;
                    EightPlayersPlugin.Log.LogInfo($"ECSNET room world: seed {msg.WorldSeed} level {msg.WorldLevel}");
                    break;

                case "spawn":
                    _world.Spawn(msg.Entity);
                    _world.Set(msg.Entity, new Owned { ClientId = msg.Owner });
                    ApplyComponents(msg.Entity, msg.Components);
                    if (msg.Tmp >= 0 && msg.Owner == _myClientId)
                    {
                        var claimed = false;
                        foreach (var lp in _locals)
                            if (lp.Tmp == msg.Tmp)
                            {
                                lp.Entity = msg.Entity;
                                lp.Tmp = -1;
                                claimed = true;
                            }
                        if (!claimed)
                            _npcs.OnSpawnAck(msg.Tmp, msg.Entity);
                    }
                    break;

                case "set":
                    ApplyComponents(msg.Entity, msg.Components);
                    break;

                case "setm":
                    if (msg.Updates != null)
                        foreach (var update in msg.Updates)
                            ApplyComponents((int)update["e"], update["components"] as JObject);
                    break;

                case "despawn":
                    _world.Despawn(msg.Entity);
                    _avatars.RemoveEntity(msg.Entity);
                    if (_ghosts.TryGetValue(msg.Entity, out var ghost))
                    {
                        if (ghost.Root != null) Destroy(ghost.Root);
                        _ghosts.Remove(msg.Entity);
                    }
                    break;

                case "peer":
                    if (msg.Joined) _peerIds.Add(msg.PeerId);
                    else _peerIds.Remove(msg.PeerId);
                    EightPlayersPlugin.Log.LogInfo($"ECSNET peer {msg.PeerName} {(msg.Joined ? "joined" : "left")}");
                    break;

                case "event":
                    ApplyEvent(msg);
                    break;

                case "error":
                    EightPlayersPlugin.Log.LogWarning($"ECSNET server error: {msg.Message}");
                    break;
            }
        }

        // World events from peers, applied through GameStateApi so they take
        // vanilla paths. Deterministic worlds mean object UIDs line up across
        // instances; a missing UID just means we're not in that level yet.
        private void ApplyEvent(ServerMsg msg)
        {
            // Every event case mutates live game state; mid-load the targets
            // don't exist yet (or belong to the dying level) and touching
            // them can kill the load coroutine. Dropped events are fine:
            // component state (hp, level, layout) reconverges after load.
            if (!WorldStable)
                return;
            switch (msg.Kind)
            {
                case "door-open":
                {
                    // Entity-addressed when the sender's reconcile map had
                    // the door (drift-immune); position is the fallback.
                    var x = (float?)msg.EventData?["x"];
                    var y = (float?)msg.EventData?["y"];
                    if (x == null || y == null)
                        return;
                    var door = ResolveWobj(msg.EventData) as Door
                        ?? GameStateApi.FindDoorAt(new Vector2(x.Value, y.Value));
                    if (door == null)
                    {
                        EightPlayersPlugin.Log.LogWarning(
                            $"ECSNET door-open from peer {msg.From} at ({x:0.#},{y:0.#}): no door there locally");
                        return;
                    }
                    try
                    {
                        door.OpenDoor(null);
                        EightPlayersPlugin.Log.LogInfo(
                            $"ECSNET door at ({x:0.#},{y:0.#}) opened by peer {msg.From} (local uid {door.UID})");
                    }
                    catch (Exception ex)
                    {
                        EightPlayersPlugin.Log.LogWarning($"ECSNET door-open apply failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    return;
                }

                case "door-lock":
                {
                    var x = (float?)msg.EventData?["x"];
                    var y = (float?)msg.EventData?["y"];
                    var locked = (bool?)msg.EventData?["locked"] ?? false;
                    if (x == null || y == null)
                        return;
                    var door = ResolveWobj(msg.EventData) as Door
                        ?? GameStateApi.FindDoorAt(new Vector2(x.Value, y.Value));
                    if (door == null)
                    {
                        EightPlayersPlugin.Log.LogWarning(
                            $"ECSNET door-lock from peer {msg.From} at ({x:0.#},{y:0.#}): no door there locally");
                        return;
                    }
                    try
                    {
                        ApplyingRemoteDoor = true;
                        if (locked) door.Lock(); else door.Unlock();
                        EightPlayersPlugin.Log.LogInfo(
                            $"ECSNET door at ({x:0.#},{y:0.#}) {(locked ? "locked" : "unlocked")} by peer {msg.From} (local uid {door.UID})");
                    }
                    catch (Exception ex)
                    {
                        EightPlayersPlugin.Log.LogWarning($"ECSNET door-lock apply failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        ApplyingRemoteDoor = false;
                    }
                    return;
                }

                case "obj-destroy":
                {
                    var x = (float?)msg.EventData?["x"];
                    var y = (float?)msg.EventData?["y"];
                    var name = (string)msg.EventData?["name"];
                    if (x == null || y == null)
                        return;
                    var obj = ResolveWobj(msg.EventData) as ObjectReal
                        ?? GameStateApi.FindObjectAt(new Vector2(x.Value, y.Value), name);
                    if (obj == null)
                    {
                        // Often benign: chain reactions publish from both
                        // sides and the loser finds the object already gone.
                        EightPlayersPlugin.Log.LogInfo(
                            $"ECSNET obj-destroy from peer {msg.From}: no '{name}' at ({x:0.#},{y:0.#}) locally (already gone?)");
                        return;
                    }
                    if (obj.destroying)
                        return;
                    try
                    {
                        ApplyingRemoteObject = true;
                        obj.DestroyMe(null);
                        EightPlayersPlugin.Log.LogInfo(
                            $"ECSNET object '{name}' at ({x:0.#},{y:0.#}) destroyed by peer {msg.From} (local uid {obj.UID})");
                    }
                    catch (Exception ex)
                    {
                        EightPlayersPlugin.Log.LogWarning($"ECSNET obj-destroy apply failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        ApplyingRemoteObject = false;
                    }
                    return;
                }

                case "fire-spawn":
                {
                    var x = (float?)msg.EventData?["x"];
                    var y = (float?)msg.EventData?["y"];
                    var oil = (bool?)msg.EventData?["oil"] ?? false;
                    if (x == null || y == null)
                        return;
                    var pos = new Vector2(x.Value, y.Value);
                    // Vanilla dedups by EXACT position; float drift across the
                    // wire needs a tolerance check against local twins (both
                    // sides simulate spread, so near-duplicates are common).
                    if (GameStateApi.FindFireAt(pos) != null)
                        return;
                    try
                    {
                        ApplyingRemoteFire = true;
                        GameStateApi.Ignite(pos, oil);
                        EightPlayersPlugin.Log.LogInfo(
                            $"ECSNET fire at ({x:0.#},{y:0.#}) ignited by peer {msg.From}");
                    }
                    catch (Exception ex)
                    {
                        EightPlayersPlugin.Log.LogWarning($"ECSNET fire-spawn apply failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        ApplyingRemoteFire = false;
                    }
                    return;
                }

                case "fire-out":
                {
                    var x = (float?)msg.EventData?["x"];
                    var y = (float?)msg.EventData?["y"];
                    if (x == null || y == null)
                        return;
                    var fire = GameStateApi.FindFireAt(new Vector2(x.Value, y.Value));
                    if (fire == null || fire.destroying)
                        return; // already out locally (burn-out publishes from both sides)
                    try
                    {
                        ApplyingRemoteFire = true;
                        fire.DestroyMe();
                        EightPlayersPlugin.Log.LogInfo(
                            $"ECSNET fire at ({x:0.#},{y:0.#}) put out by peer {msg.From}");
                    }
                    catch (Exception ex)
                    {
                        EightPlayersPlugin.Log.LogWarning($"ECSNET fire-out apply failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        ApplyingRemoteFire = false;
                    }
                    return;
                }

                case "pvp-hit":
                {
                    // Damage aimed at MY player's avatar elsewhere: I am the
                    // authority for my own hp — apply through vanilla
                    // ChangeHealth, which republishes the hp component.
                    var entity = (int?)msg.EventData?["e"] ?? -1;
                    var dmg = (float?)msg.EventData?["dmg"] ?? 0f;
                    if (entity < 0 || dmg == 0f)
                        return;
                    foreach (var lp in _locals)
                        if (lp.Entity == entity && lp.Agent != null)
                        {
                            lp.Agent.statusEffects.ChangeHealth(dmg);
                            EightPlayersPlugin.Log.LogInfo(
                                $"ECSNET pvp hit from peer {msg.From}: {dmg:0.#} hp -> {lp.Agent.health:0.#}");
                            return;
                        }
                    return;
                }

                case "status":
                {
                    // A remote player's status changed: mirror it on their
                    // avatar (visual/vanilla effects, no text popups).
                    var entity = (int?)msg.EventData?["e"] ?? -1;
                    var name = (string)msg.EventData?["name"];
                    var on = (bool?)msg.EventData?["on"] ?? false;
                    var target = _avatars.GetAgentFor(entity);
                    if (target == null || string.IsNullOrEmpty(name) || target.dead)
                        return;
                    try
                    {
                        if (on)
                            target.statusEffects.AddStatusEffect(name, showText: false);
                        else
                            target.statusEffects.RemoveStatusEffect(name, showText: false, playSound: false);
                        EightPlayersPlugin.Log.LogInfo(
                            $"ECSNET status '{name}' {(on ? "on" : "off")} applied to avatar (entity {entity})");
                    }
                    catch (Exception ex)
                    {
                        EightPlayersPlugin.Log.LogWarning($"ECSNET status apply failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    return;
                }

                case "item-pickup":
                {
                    var x = (float?)msg.EventData?["x"];
                    var y = (float?)msg.EventData?["y"];
                    var name = (string)msg.EventData?["name"];
                    if (x == null || y == null || name == null)
                        return;
                    var item = GameStateApi.FindGroundItemAt(new Vector2(x.Value, y.Value), name);
                    if (item == null)
                    {
                        EightPlayersPlugin.Log.LogWarning(
                            $"ECSNET item-pickup '{name}' at ({x:0.#},{y:0.#}) from peer {msg.From}: not found locally");
                        return;
                    }
                    try
                    {
                        item.DestroyMeFromClient();
                        EightPlayersPlugin.Log.LogInfo($"ECSNET ground item '{name}' at ({x:0.#},{y:0.#}) taken by peer {msg.From}");
                    }
                    catch (Exception ex)
                    {
                        EightPlayersPlugin.Log.LogWarning($"ECSNET item-pickup apply failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    return;
                }

                case "item-drop":
                {
                    var x = (float?)msg.EventData?["x"];
                    var y = (float?)msg.EventData?["y"];
                    var name = (string)msg.EventData?["name"];
                    if (x == null || y == null || name == null)
                        return;
                    try
                    {
                        GameStateApi.SpawnGroundItem(new Vector2(x.Value, y.Value), name);
                        EightPlayersPlugin.Log.LogInfo($"ECSNET ground item '{name}' dropped at ({x:0.#},{y:0.#}) by peer {msg.From}");
                    }
                    catch (Exception ex)
                    {
                        EightPlayersPlugin.Log.LogWarning($"ECSNET item-drop apply failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    return;
                }
            }
        }

        // ---- NpcSync plumbing ----

        internal int SendNpcSpawn(int index, string type, Vector2 pos)
        {
            var tmp = _nextTmp++;
            _client.Send(Protocol.Spawn(tmp, Protocol.NpcComponents(index, type, pos.x, pos.y)));
            return tmp;
        }

        internal void SendPos(int entity, Vector2 pos) =>
            _client.Send(Protocol.Set(entity, Protocol.PosComponent(pos.x, pos.y)));

        internal void SendHp(int entity, float cur, float max) =>
            _client.Send(Protocol.Set(entity, Protocol.HpComponent(cur, max)));

        internal void SendBatch(JArray updates) =>
            _client.Send(Protocol.SetBatch(updates));

        internal void SendDead(int entity, float cur, float max)
        {
            var components = Protocol.HpComponent(cur, max);
            components.Merge(Protocol.DeadComponent());
            _client.Send(Protocol.Set(entity, components));
            EightPlayersPlugin.Log.LogInfo($"ECSNET npc death published for entity {entity}");
        }

        internal void SendDespawn(int entity) =>
            _client.Send(Protocol.Despawn(entity));

        internal void SendSpawnRaw(JObject components) =>
            _client.Send(Protocol.Spawn(_nextTmp++, components));

        // ---- debug-harness ECS surface (CommandChannel) ------------------

        /// <summary>Raw component write to any entity we own (the DO rejects
        /// writes to others'). The harness's generic mutation verb.</summary>
        public void HarnessSet(int entity, JObject components) =>
            _client.Send(Protocol.Set(entity, components));

        /// <summary>Inject a named event into the room, exactly as a system
        /// publisher would.</summary>
        public void HarnessEvent(string name, JObject data) =>
            _client.Send(Protocol.Event(name, data));

        /// <summary>Full-fidelity view of one entity (verbatim merged JSON).</summary>
        public string HarnessGet(int entity)
        {
            if (!_world.Raw.TryGetValue(entity, out var raw))
                return _world.Exists(entity) ? "{}" : null;
            return raw.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>One line per entity: id, owner, merged component JSON.</summary>
        public IEnumerable<string> HarnessEntities()
        {
            foreach (var e in _world.Entities)
            {
                int owner = _world.TryGet<Owned>(e, out var o) ? o.ClientId : -1;
                _world.Raw.TryGetValue(e, out var raw);
                yield return $"entity {e} owner={owner} {raw?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}"}";
            }
        }

        /// <summary>Called from the SpawnAgent choke-point hook.</summary>
        public void RegisterNpcSpawn(Agent agent)
        {
            var gc = GameController.gameController;
            if (gc == null || agent == null || agent.isPlayer != 0)
                return;
            // Pseudo-agents (ObjectAgent = object-interaction helper, dummies)
            // are transient implementation details, not characters — never
            // sync them. Filter runs identically everywhere, so the
            // spawn-index registries stay aligned.
            if (agent.isDummy || agent.agentName == "ObjectAgent")
                return;
            if (!gc.loadComplete)
            {
                _npcs.Register(agent);
                return;
            }
            // Post-load spawn: if we're the authority, publish it as a
            // dynamic NPC — unless it's one of our own deliberate spawns
            // (avatars / remote mirrors), which must stay local.
            if (_welcomed && _wasNpcAuthority && !NpcSync.BypassSuppression
                && EightPlayersPlugin.EcsNpcSync.Value)
            {
                _npcs.RegisterDynamic(agent);
                EightPlayersPlugin.Log.LogInfo($"ECSNET dynamic npc registered: {agent.agentName} (uid {agent.UID})");
            }
        }

        /// <summary>Followers in a synced world suppress game-initiated dynamic NPC spawns.</summary>
        public bool ShouldSuppressDynamicSpawn
        {
            get
            {
                var gc = GameController.gameController;
                return _welcomed
                    && !_wasNpcAuthority
                    && AdoptedSeed != null
                    && EightPlayersPlugin.EcsNpcSync.Value
                    && EightPlayersPlugin.EcsSuppressDynamicSpawns.Value
                    && gc != null && gc.loadComplete;
            }
        }

        /// <summary>Called from the level-generation choke-point hook.</summary>
        public void OnLevelGenerated()
        {
            GameStateApi.InvalidateWorldHash();
            _npcs.OnLevelGenerated();
            _wobjs?.OnLevelChanged();
        }

        /// <summary>Called from the Item.Interact choke-point hook (EcsHooks).</summary>
        public void OnLocalItemPickup(string itemName, Vector2 pos)
        {
            if (_welcomed && !string.IsNullOrEmpty(itemName))
                _client.Send(Protocol.Event("item-pickup",
                    new JObject { ["x"] = pos.x, ["y"] = pos.y, ["name"] = itemName }));
        }

        /// <summary>Called from the InvDatabase.DropItem choke-point hook (EcsHooks).</summary>
        public void OnLocalItemDrop(Item worldItem)
        {
            if (!_welcomed || worldItem == null || worldItem.invItem == null || worldItem.tr == null)
                return;
            Vector2 p = worldItem.tr.position;
            _client.Send(Protocol.Event("item-drop",
                new JObject { ["x"] = p.x, ["y"] = p.y, ["name"] = worldItem.invItem.invItemName }));
        }

        /// <summary>Called from the Door.OpenDoor choke-point hook (EcsHooks).</summary>
        public void OnLocalDoorOpen(Door door)
        {
            if (!_welcomed || door == null || door.tr == null)
                return;
            Vector2 p = door.tr.position;
            _client.Send(Protocol.Event("door-open", WithWobjEntity(door,
                new JObject { ["x"] = p.x, ["y"] = p.y })));
        }

        /// <summary>Adds the layout INDEX to a world-object event payload
        /// when reconciliation has one — receivers prefer it over position
        /// (immune to generation drift). Position stays as the fallback.</summary>
        private JObject WithWobjEntity(PlayfieldObject obj, JObject payload)
        {
            int i = _wobjs != null ? _wobjs.IndexFor(obj) : -1;
            if (i >= 0)
                payload["wi"] = i;
            return payload;
        }

        /// <summary>Receiver side of WithWobjEntity: the local twin from the
        /// reconcile map, or null (caller falls back to position lookup).</summary>
        private PlayfieldObject ResolveWobj(JObject eventData)
        {
            var i = (int?)eventData?["wi"] ?? -1;
            return i >= 0 && _wobjs != null ? _wobjs.ObjectAt(i) : null;
        }

        /// <summary>True while a remote door event is being applied locally,
        /// so the Lock/Unlock hooks don't echo it back into the room.</summary>
        public static bool ApplyingRemoteDoor;

        /// <summary>Same suppression for the ObjectReal.DestroyMe hook.</summary>
        public static bool ApplyingRemoteObject;

        /// <summary>Same suppression for the SpawnFire / Fire.DestroyMe hooks.</summary>
        public static bool ApplyingRemoteFire;

        // World-object events only publish while the level is fully loaded:
        // generation and teardown mutate objects (setup locks doors, clears
        // props...) but that state is seed-derived — every instance produces
        // it locally, and peers mid-load can't resolve the positions anyway.
        // loadCompleteReally, NOT loadComplete: the latter is a Mirror-era
        // flag that stays false after a solo-mode follow reload (observed
        // live), which silently killed every gated publisher on followers.
        private static bool WorldStable
        {
            get
            {
                var gc = GameController.gameController;
                return gc != null && gc.loadCompleteReally && !gc.levelTransitioning;
            }
        }

        /// <summary>Called from the SpawnFire hook (EcsHooks).</summary>
        public void OnLocalFireSpawned(Fire fire, bool oil)
        {
            if (!_welcomed || fire == null || fire.tr == null || !WorldStable)
                return;
            Vector2 p = fire.tr.position;
            _client.Send(Protocol.Event("fire-spawn",
                new JObject { ["x"] = p.x, ["y"] = p.y, ["oil"] = oil }));
        }

        /// <summary>Called from the Fire.DestroyMe hook (EcsHooks).</summary>
        public void OnLocalFireOut(Fire fire)
        {
            if (!_welcomed || fire == null || fire.tr == null || !WorldStable)
                return;
            Vector2 p = fire.tr.position;
            _client.Send(Protocol.Event("fire-out", new JObject { ["x"] = p.x, ["y"] = p.y }));
        }

        /// <summary>Called from the ObjectReal.DestroyMe hook (EcsHooks).</summary>
        public void OnLocalObjectDestroyed(ObjectReal obj)
        {
            if (!_welcomed || obj == null || obj.tr == null || !WorldStable)
                return;
            Vector2 p = obj.tr.position;
            _client.Send(Protocol.Event("obj-destroy", WithWobjEntity(obj,
                new JObject { ["x"] = p.x, ["y"] = p.y, ["name"] = obj.objectName })));
        }

        /// <summary>Called from the Door.Lock/Unlock hooks (EcsHooks).</summary>
        public void OnLocalDoorLock(Door door, bool locked)
        {
            if (!_welcomed || door == null || door.tr == null || !WorldStable)
                return;
            Vector2 p = door.tr.position;
            _client.Send(Protocol.Event("door-lock", WithWobjEntity(door,
                new JObject { ["x"] = p.x, ["y"] = p.y, ["locked"] = locked })));
        }

        private void ApplyComponents(int e, JObject components)
        {
            if (components == null)
                return;
            _world.MergeRaw(e, components);
            if (components["pos"] is JObject pos)
                _world.Set(e, new Pos { X = (float)pos["x"], Y = (float)pos["y"] });
            if (components["player"] is JObject player)
                _world.Set(e, new PlayerInfo
                {
                    Name = (string)player["name"],
                    Color = (int?)player["color"] ?? 0,
                    Char = (string)player["char"],
                });
            if (components["hp"] is JObject hp)
                _world.Set(e, new Hp { Cur = (float?)hp["cur"] ?? 0, Max = (float?)hp["max"] ?? 0 });
            if (components["level"] is JObject level)
                _world.Set(e, new LevelId
                {
                    Seed = (int?)level["seed"] ?? 0,
                    Num = (int?)level["num"] ?? 0,
                    Hash = (uint?)level["hash"] ?? 0,
                });
            if (components["npc"] is JObject npc)
                _world.Set(e, new NpcTag { Index = (int?)npc["i"] ?? -1, Type = (string)npc["type"] });
            // The world-object layout ("wlayout") is read straight from the
            // Raw store by WorldObjects — no typed component needed.
            if (components["weapon"] is JObject weapon)
                _world.Set(e, new WeaponInfo { Name = (string)weapon["name"] });
            if (components["dead"] is JObject)
                _world.Set(e, new DeadTag { Value = true });
        }

        // ---- outgoing ----

        /// <summary>Called from the IncreaseLevel choke-point hook (EcsHooks).</summary>
        public void OnLocalLevelAdvance()
        {
            var gc = GameController.gameController;
            if (!_welcomed || gc == null || gc.sessionDataBig == null)
                return;
            var num = gc.sessionDataBig.curLevel;
            if (num > RoomLevel)
            {
                RoomLevel = num;
                _client.Send(Protocol.WorldNum(num));
                EightPlayersPlugin.Log.LogInfo($"ECSNET advancing room to level {num}");
            }
        }

        // The room's party moved ahead of us: take the vanilla next-level
        // path to catch up (same seed -> same world). One step per cooldown;
        // multi-level gaps are closed one transition at a time.
        private void FollowRoomLevel()
        {
            if (!EightPlayersPlugin.EcsFollowLevel.Value || AdoptedSeed == null)
                return;
            var gc = GameController.gameController;
            if (gc == null || gc.sessionDataBig == null || gc.loadLevel == null)
                return;
            if (gc.sessionDataBig.curLevel >= RoomLevel || _locals.Count == 0)
                return;
            if (Time.unscaledTime < _nextFollowAt)
                return;
            _nextFollowAt = Time.unscaledTime + 20f;
            EightPlayersPlugin.Log.LogInfo(
                $"ECSNET following room: level {gc.sessionDataBig.curLevel} -> {RoomLevel}");
            gc.loadLevel.NextLevel();
        }

        private void PublishLocalPlayers()
        {
            SyncLocalAgentList();
            ClaimWorldSeedIfFirst();
            HealWorldDivergence();
            FollowRoomLevel();
            if (_welcomed && WorldStable
                && System.Environment.GetEnvironmentVariable("SOR_WOBJ") != "0")
            {
                var gcw = GameController.gameController;
                if (_wobjs == null)
                    _wobjs = new WorldObjects(_world, this);
                if (gcw != null && gcw.sessionDataBig != null)
                    _wobjs.Tick(_wasNpcAuthority, gcw.sessionDataBig.curLevel);
            }

            for (int i = 0; i < _locals.Count; i++)
            {
                var lp = _locals[i];
                if (lp.Agent == null)
                {
                    if (lp.Entity >= 0)
                        _client.Send(Protocol.Despawn(lp.Entity));
                    _locals.RemoveAt(i--);
                    continue;
                }

                Vector2 p = lp.Agent.tr.position;
                if (lp.Entity < 0)
                {
                    if (lp.Tmp >= 0)
                        continue; // spawn in flight
                    lp.Tmp = _nextTmp++;
                    var name = _locals.Count > 1 ? $"{PlayerName}.{i + 1}" : PlayerName;
                    var components = Protocol.PlayerComponents(name, i + 1, lp.Agent.agentName, p.x, p.y, lp.Agent.health, lp.Agent.healthMax);
                    var here = LocalLevel();
                    components.Merge(Protocol.LevelComponent(here.Seed, here.Num, here.Hash));
                    _client.Send(Protocol.Spawn(lp.Tmp, components));
                    lp.LastSent = p;
                    lp.HpDirty = false;
                    lp.SentLevel = here;
                    lp.SentChar = lp.Agent.agentName;
                }
                else
                {
                    if ((p - lp.LastSent).sqrMagnitude > MinMove * MinMove
                        || Time.unscaledTime >= lp.NextPosKeepalive)
                    {
                        _client.Send(Protocol.Set(lp.Entity, Protocol.PosComponent(p.x, p.y)));
                        lp.LastSent = p;
                        lp.NextPosKeepalive = Time.unscaledTime + PosKeepaliveSeconds;
                    }
                    if (lp.HpDirty)
                    {
                        _client.Send(Protocol.Set(lp.Entity, Protocol.HpComponent(lp.Agent.health, lp.Agent.healthMax)));
                        lp.HpDirty = false;
                    }
                    // Level or character changed since last publish (level gen
                    // finished, elevator taken, char picked/transformed):
                    // refresh the slow-changing components.
                    var now = LocalLevel();
                    if (now.Seed != lp.SentLevel.Seed || now.Num != lp.SentLevel.Num
                        || now.Hash != lp.SentLevel.Hash || lp.Agent.agentName != lp.SentChar)
                    {
                        var name = _locals.Count > 1 ? $"{PlayerName}.{i + 1}" : PlayerName;
                        var refresh = Protocol.PlayerComponents(name, i + 1, lp.Agent.agentName, p.x, p.y, lp.Agent.health, lp.Agent.healthMax);
                        refresh.Merge(Protocol.LevelComponent(now.Seed, now.Num, now.Hash));
                        _client.Send(Protocol.Set(lp.Entity, refresh));
                        lp.SentLevel = now;
                        lp.SentChar = lp.Agent.agentName;
                    }
                }
            }
        }

        // First client to reach a generated level claims the room's world
        // seed (the DO enforces first-write-wins and reminds losers of the
        // real seed). Everyone else's next game start adopts it via
        // ForceSeed_Patch.
        private void ClaimWorldSeedIfFirst()
        {
            if (_worldClaimSent || AdoptedSeed != null)
                return;
            var gc = GameController.gameController;
            var letter = gc != null && gc.loadLevel != null ? gc.loadLevel.randomSeedLetter : null;
            if (string.IsNullOrEmpty(letter))
                return;
            _client.Send(Protocol.World(letter));
            _worldClaimSent = true;
            EightPlayersPlugin.Log.LogInfo($"ECSNET claiming room world seed: {letter}");
        }

        private void SyncLocalAgentList()
        {
            var gc = GameController.gameController;
            if (gc == null || gc.playerAgentList == null)
                return;
            foreach (var agent in gc.playerAgentList)
            {
                if (agent == null || agent.isPlayer == 0)
                    continue;
                bool known = false;
                foreach (var lp in _locals)
                    if (ReferenceEquals(lp.Agent, agent))
                    {
                        known = true;
                        break;
                    }
                if (!known)
                    _locals.Add(new LocalPlayer { Agent = agent });
            }
        }

        // ---- ghosts ----

        private static LevelId LocalLevel()
        {
            var gc = GameController.gameController;
            return new LevelId
            {
                Seed = gc != null && gc.loadLevel != null ? gc.loadLevel.randomSeedNum : 0,
                Num = gc != null && gc.sessionDataBig != null ? gc.sessionDataBig.curLevel : 0,
                Hash = GameStateApi.WorldHash(),
            };
        }

        // ---- world divergence healing -----------------------------------
        //
        // Same seed + same level number SHOULD give identical geometry, but
        // level generation has frame-timing-dependent RNG consumption that
        // occasionally shifts object placement (observed live 2026-07-08:
        // one instance had a Table where the other had a FlamingBarrel; all
        // positional sync silently broke). The level component carries a
        // door-geometry hash; if a LOWER client id shares our seed+num with
        // a different hash, their world wins and we regenerate ours by
        // stepping curLevel back and letting FollowRoomLevel re-run the
        // vanilla next-level path (which re-forces the seed). Bounded so a
        // genuinely irreconcilable pair can't reload forever.

        private int _healSeed, _healNum, _healCount;
        private float _nextHealAt;

        private void HealWorldDivergence()
        {
            var gc = GameController.gameController;
            if (gc == null || gc.sessionDataBig == null || !gc.loadCompleteReally || gc.levelTransitioning)
                return;
            if (Time.unscaledTime < _nextHealAt)
                return;
            var mine = LocalLevel();
            if (mine.Hash == 0)
                return;
            bool diverged = false;
            int authorityClient = int.MaxValue;
            _world.ForEach<LevelId>((e, lvl) =>
            {
                if (!_world.TryGet<Owned>(e, out var owned) || owned.ClientId == _myClientId)
                    return;
                if (!_world.TryGet<PlayerInfo>(e, out _))
                    return; // only players carry authoritative level hashes
                if (owned.ClientId < _myClientId && owned.ClientId < authorityClient
                    && lvl.Seed == mine.Seed && lvl.Num == mine.Num && lvl.Hash != 0)
                {
                    authorityClient = owned.ClientId;
                    diverged = lvl.Hash != mine.Hash;
                }
            });
            if (!diverged)
                return;
            _nextHealAt = Time.unscaledTime + 45f;
            if (_healSeed != mine.Seed || _healNum != mine.Num)
            {
                _healSeed = mine.Seed;
                _healNum = mine.Num;
                _healCount = 0;
            }
            if (_healCount >= 2)
            {
                EightPlayersPlugin.Log.LogError(
                    $"ECSNET world diverged from client {authorityClient} (hash {mine.Hash:x8}) and 2 reloads did not converge - positional sync degraded");
                return;
            }
            _healCount++;
            EightPlayersPlugin.Log.LogWarning(
                $"ECSNET world diverged from client {authorityClient} on level {mine.Num} (my hash {mine.Hash:x8}) - regenerating (attempt {_healCount}/2)");
            // Step back one level; FollowRoomLevel's next tick replays the
            // vanilla transition into the room's level with the forced seed.
            gc.sessionDataBig.curLevel -= 1;
        }

        private void UpdateGhosts()
        {
            var here = LocalLevel();
            _world.ForEach<PlayerInfo>((e, info) =>
            {
                if (_world.TryGet<Owned>(e, out var owned) && owned.ClientId == _myClientId)
                    return;
                if (!_world.TryGet<Pos>(e, out var pos))
                    return;

                if (!_ghosts.TryGetValue(e, out var ghost) || ghost.Root == null)
                {
                    ghost = CreateGhost(e, info);
                    _ghosts[e] = ghost;
                }

                // Only show players who are in the same generated world.
                var sameLevel = !_world.TryGet<LevelId>(e, out var lvl)
                    || (lvl.Seed == here.Seed && lvl.Num == here.Num);
                if (ghost.Root.activeSelf != sameLevel)
                    ghost.Root.SetActive(sameLevel);
                ghost.Target = new Vector2(pos.X, pos.Y);
                if (ghost.Label != null)
                    ghost.Label.text = _world.TryGet<Hp>(e, out var hp)
                        ? $"{info.Name} {hp.Cur:0}/{hp.Max:0}"
                        : info.Name;
            });

            foreach (var ghost in _ghosts.Values)
            {
                if (ghost.Root == null)
                    continue;
                var current = (Vector2)ghost.Root.transform.position;
                var next = Vector2.Lerp(current, ghost.Target, 12f * Time.unscaledDeltaTime);
                ghost.Root.transform.position = new Vector3(next.x, next.y, -0.1f);
            }
        }

        private Ghost CreateGhost(int e, PlayerInfo info)
        {
            var root = new GameObject($"EcsGhost_{e}");
            DontDestroyOnLoad(root);

            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            var renderer = body.AddComponent<SpriteRenderer>();
            renderer.sprite = GhostSprite();
            renderer.color = GhostColor(info.Color);
            renderer.sortingOrder = 999;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(root.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            labelGo.transform.localScale = Vector3.one * 0.12f;
            var label = labelGo.AddComponent<TextMesh>();
            label.text = info.Name ?? $"entity {e}";
            label.anchor = TextAnchor.LowerCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 48;
            label.color = Color.white;
            labelGo.GetComponent<MeshRenderer>().sortingOrder = 1000;

            return new Ghost { Root = root, Label = label, Target = Vector2.zero };
        }

        private Sprite GhostSprite()
        {
            if (_ghostSprite != null)
                return _ghostSprite;
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float r = size / 2f - 1f, cx = size / 2f - 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx, dy = y - cx;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    var a = d < r - 1f ? 0.85f : (d < r ? 0.85f * (r - d) : 0f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            _ghostSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 48f);
            return _ghostSprite;
        }

        private static Color GhostColor(int index)
        {
            switch (((index % 8) + 8) % 8)
            {
                case 0: return new Color(0.4f, 0.8f, 1f);
                case 1: return new Color(1f, 0.5f, 0.4f);
                case 2: return new Color(0.5f, 1f, 0.5f);
                case 3: return new Color(1f, 0.9f, 0.3f);
                case 4: return new Color(0.9f, 0.5f, 1f);
                case 5: return new Color(1f, 0.7f, 0.2f);
                case 6: return new Color(0.4f, 1f, 0.9f);
                default: return new Color(0.8f, 0.8f, 0.8f);
            }
        }

        public IEnumerable<string> DescribeNpcRegistry() => _npcs.DescribeRegistry();

        /// <summary>Multi-line dump of the ECS session for the command channel (`ecs`).</summary>
        public string DebugDump()
        {
            var sb = new System.Text.StringBuilder();
            var here = LocalLevel();
            sb.AppendLine($"state={_client?.State} welcomed={_welcomed} me={_myClientId} room={RoomCode}");
            sb.AppendLine($"adoptedSeed={AdoptedSeed ?? "(none)"} localLevel={here.Seed}/{here.Num} avatars={EightPlayersPlugin.EcsRealAvatars.Value}");
            sb.AppendLine($"npcAuthority={IsNpcAuthority} registeredNpcs={_npcs.RegisteredCount} peers=[{string.Join(",", _peerIds)}]");
            foreach (var lp in _locals)
                sb.AppendLine($"  local agent={(lp.Agent == null ? "null" : lp.Agent.UID.ToString())} entity={lp.Entity} tmp={lp.Tmp}");
            foreach (var e in _world.Entities)
            {
                _world.TryGet<Owned>(e, out var owned);
                var parts = $"  entity {e} owner={owned.ClientId}";
                if (_world.TryGet<PlayerInfo>(e, out var pi)) parts += $" player={pi.Name}/{pi.Char}/c{pi.Color}";
                if (_world.TryGet<Pos>(e, out var pos)) parts += $" pos=({pos.X:0.#},{pos.Y:0.#})";
                if (_world.TryGet<LevelId>(e, out var lvl)) parts += $" level={lvl.Seed}/{lvl.Num}";
                if (_world.TryGet<Hp>(e, out var hp)) parts += $" hp={hp.Cur:0.#}/{hp.Max:0.#}";
                sb.AppendLine(parts);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Join a room by code (UI button / `room` command). Persists to config.</summary>
        public void JoinRoom(string code)
        {
            code = (code ?? "").Trim().ToUpperInvariant();
            if (code.Length == 0)
                return;
            EightPlayersPlugin.EcsRoom.Value = code;
            _nextConnectAt = 0f; // connect on the next frame
            EightPlayersPlugin.Log.LogInfo($"ECSNET joining room {code}");
        }

        /// <summary>Leave the current room (UI button / `leave` command).</summary>
        public void LeaveRoom()
        {
            EightPlayersPlugin.Log.LogInfo("ECSNET leaving room");
            EightPlayersPlugin.EcsRoom.Value = "";
        }

        private bool _showRoomUi;
        private string _roomUiCode = "";
        private string _roomUiName = "";
        private Rect _roomUiRect = new Rect(60, 60, 340, 170);

        private void RoomWindow(int id)
        {
            GUILayout.Label(Enabled
                ? $"In room {RoomCode} ({(_client?.State == NetState.Connected ? "connected" : "connecting")})"
                : "Not in a room");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Room", GUILayout.Width(50));
            _roomUiCode = GUILayout.TextField(_roomUiCode, 32);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(50));
            _roomUiName = GUILayout.TextField(_roomUiName, 32);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Join") && _roomUiCode.Trim().Length > 0)
            {
                if (_roomUiName.Trim().Length > 0)
                    EightPlayersPlugin.EcsPlayerName.Value = _roomUiName.Trim();
                JoinRoom(_roomUiCode);
                _showRoomUi = false;
            }
            if (GUILayout.Button("Leave"))
                LeaveRoom();
            if (GUILayout.Button("Close"))
                _showRoomUi = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("Everyone in a room shares one world (start a NEW game after joining).");
            GUI.DragWindow();
        }

        private void OnGUI()
        {
            if (_showRoomUi)
                _roomUiRect = GUI.Window(0xEC5, _roomUiRect, RoomWindow, "Co-op room (F9)");
            if (!EightPlayersPlugin.EcsShowHud.Value)
                return;
            if (!Enabled || _client == null)
            {
                if (!_showRoomUi)
                    GUI.Label(new Rect(8, 8, 640, 22), "ECSNET off — press F9 to join a co-op room");
                return;
            }
            var status = _client.State == NetState.Connected
                ? (_welcomed ? $"room {RoomCode} · client {_myClientId} · {_world.Count} entities" : "handshaking")
                : _client.State.ToString().ToLowerInvariant();
            if (AdoptedSeed != null)
            {
                var gc = GameController.gameController;
                var localLetter = gc != null && gc.loadLevel != null ? gc.loadLevel.randomSeedLetter : "";
                status += localLetter == AdoptedSeed
                    ? $" · world {AdoptedSeed}"
                    : $" · room world is {AdoptedSeed} — start a NEW game to sync maps";
            }
            GUI.Label(new Rect(8, 8, 960, 22), $"ECSNET {status}");
        }
    }
}
