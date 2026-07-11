using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers
{
    // Programmatic write-access to live game state, through the same choke
    // points the trace layer observes (docs/trace-choke-points.md). Everything
    // here must be called on the Unity main thread.
    //
    // Two consumers:
    //  - CommandChannel verbs (spawnagent/hp/kill/give/drop/tp/opendoor),
    //    letting test scripts drive the game and assert on the trace output.
    //  - The ECS apply-path: remote component changes land in local game
    //    state through these calls.
    public static class GameStateApi
    {
        private static GameController GC => GameController.gameController;

        public static Agent FindAgent(int uid)
        {
            var gc = GC;
            if (gc == null)
                return null;
            foreach (var agent in gc.agentList)
                if (agent != null && agent.UID == uid)
                    return agent;
            return null;
        }

        /// <summary>The live player-N agent (1-based). Uids churn during level
        /// load, so target aliases (player:<n>) resolve through this each call.</summary>
        public static Agent FindPlayer(int n)
        {
            var gc = GC;
            if (gc == null)
                return null;
            int i = 0;
            foreach (var agent in gc.playerAgentList)
                if (agent != null && ++i == n)
                    return agent;
            return null;
        }

        /// <summary>Resolve an agent spec from a command arg: a numeric uid, or
        /// the stable aliases "player" / "player:N" (uids churn even on settled
        /// levels — aliases resolve live each call).</summary>
        public static int ResolveUid(string spec)
        {
            if (spec.StartsWith("player", System.StringComparison.OrdinalIgnoreCase))
            {
                int n = 1;
                int colon = spec.IndexOf(':');
                if (colon >= 0 && !int.TryParse(spec.Substring(colon + 1), out n))
                    throw new System.ArgumentException($"bad player alias '{spec}'");
                var p = FindPlayer(n < 1 ? 1 : n);
                if (p == null)
                    throw new System.ArgumentException($"no player {n}");
                return p.UID;
            }
            return int.Parse(spec);
        }

        public static IEnumerable<Agent> Agents()
        {
            var gc = GC;
            if (gc == null)
                yield break;
            foreach (var agent in gc.agentList)
                if (agent != null)
                    yield return agent;
        }

        public static string DescribeAgent(Agent a)
        {
            if (a == null)
                return "null";
            Vector2 p = a.tr.position;
            var name = string.IsNullOrEmpty(a.agentRealName) ? a.agentName : a.agentRealName;
            return $"uid={a.UID} '{name}' type={a.agentName} pos=({p.x:0.##},{p.y:0.##}) hp={a.health:0.#}/{a.healthMax:0.#} player={a.isPlayer} dead={a.dead}";
        }

        public static Agent SpawnAgent(string agentType, Vector2 pos, int playerColor = 0)
        {
            var gc = GC;
            if (gc == null || gc.spawnerMain == null)
                throw new InvalidOperationException("no game running");
            // Deliberate spawns (avatars, remote applies, command verbs) must
            // not be eaten by the follower-side dynamic-spawn suppression.
            try
            {
                EcsNet.NpcSync.BypassSuppression = true;
                var v = new Vector3(pos.x, pos.y, 0f);
                if (playerColor != 0)
                    // Player-body path: the playerColor overload delegates with
                    // multPlayer:true — input-driven, no NPC brain wanted.
                    return gc.spawnerMain.SpawnAgent(v, agentType, playerColor);
                // NPC path (rogue-gm#5): use the (pos, agentSource, agentType)
                // overload — the same call the game's own mid-level event
                // spawners (LevelFeelings) use. It delegates with
                // multPlayer:false, which runs SetupAgentStats("") and starts
                // BrainUpdate.StartBrain(...), registering the agent into
                // LoadLevel.AIOffsetGroups — the list whose coroutine ticks
                // agent brains. The playerColor overload we used before
                // delegates with multPlayer:true (Mirror's remote player-body
                // path) and skips BOTH, so spawned NPCs stood frozen forever.
                // Callers that want an inert body (avatars, dynamic mirrors)
                // already deactivate the brain explicitly after spawn.
                var spawned = gc.spawnerMain.SpawnAgent(v, (PlayfieldObject)null, agentType);
                // Force the brain ACTIVE immediately. StartBrain flips
                // brain.active over a coroutine, so a spawn read back the same
                // frame (or a body that landed in geometry) can show
                // active=false and sit frozen. Registering it into the
                // active-brain lists now makes spawned characters live on the
                // very next AI tick — the expected default for anything the
                // GM/characters spawn. (Inert-body callers still override with
                // SetBrainActive(false) / Lua takeControl(false) afterward.)
                ActivateBrain(spawned);
                // StartBrain flips brain.active over a coroutine that runs on
                // later frames and can overwrite our activation, so re-assert
                // for a few frames after spawn. (Idempotent; a caller that
                // wants an inert body deactivates after and the reassert
                // coroutine no-ops once the agent is gone.)
                if (spawned != null)
                    EightPlayersPlugin.Instance.StartCoroutine(ReassertActive(spawned));
                return spawned;
            }
            finally
            {
                EcsNet.NpcSync.BypassSuppression = false;
            }
        }

        /// <summary>Positive heals, negative damages — same convention as StatusEffects.ChangeHealth.</summary>
        public static float ChangeHealth(int uid, float delta)
        {
            var agent = Require(uid);
            agent.statusEffects.ChangeHealth(delta);
            return agent.health;
        }

        public static void Kill(int uid)
        {
            var agent = Require(uid);
            agent.statusEffects.SetupDeath(null, killedOnClient: false, noSFX: false);
        }

        /// <summary>Trigger the agent's in-game speech bubble — the vanilla
        /// Agent.Say choke point (SpawnTalkText). importantText makes the bubble
        /// linger and, in netplay, relay to clients.</summary>
        public static void Say(int uid, string message, bool important = true)
        {
            var agent = Require(uid);
            agent.Say(message, important);
        }

        public static void SetStatus(int uid, string effect, bool on)
        {
            var agent = Require(uid);
            if (on)
                agent.statusEffects.AddStatusEffect(effect);
            else
                agent.statusEffects.RemoveStatusEffect(effect);
        }

        public static IEnumerable<string> Statuses(int uid)
        {
            var agent = Require(uid);
            foreach (var se in agent.statusEffects.StatusEffectList)
                if (se != null)
                    yield return se.statusEffectName;
        }

        public static void GiveItem(int uid, string itemName, int count)
        {
            var agent = Require(uid);
            var item = agent.inventory.AddItem(itemName, count);
            if (item == null)
                throw new ArgumentException($"item '{itemName}' not created");
        }

        /// <summary>Give (if absent) and equip a weapon through the vanilla
        /// EquipWeapon choke point.</summary>
        public static void EquipWeapon(int uid, string itemName)
        {
            var agent = Require(uid);
            InvItem item = null;
            foreach (var it in agent.inventory.InvItemList)
                if (it != null && it.invItemName == itemName)
                    item = it;
            if (item == null)
                item = agent.inventory.AddItem(itemName, 1);
            if (item == null)
                throw new ArgumentException($"item '{itemName}' not created");
            agent.inventory.EquipWeapon(item, sfx: false);
        }

        public static void DropItem(int uid, string itemName)
        {
            var agent = Require(uid);
            InvItem found = null;
            foreach (var item in agent.inventory.InvItemList)
                if (item != null && string.Equals(item.invItemName, itemName, StringComparison.OrdinalIgnoreCase))
                    found = item;
            if (found == null)
                throw new ArgumentException($"agent {uid} has no '{itemName}'");
            agent.inventory.DropItem(found);
        }

        public static void Teleport(int uid, Vector2 pos)
        {
            var agent = Require(uid);
            // Movement.Teleport alone gets reverted for player-controlled
            // agents; move the physics body and transform along with it.
            agent.movement.Teleport(pos);
            agent.tr.position = new Vector3(pos.x, pos.y, agent.tr.position.z);
            if (agent.rigidBody2D != null)
                agent.rigidBody2D.position = pos;
        }

        public static IEnumerable<Door> Doors()
        {
            var gc = GC;
            if (gc == null)
                yield break;
            foreach (var objectReal in gc.objectRealList)
                if (objectReal is Door door)
                    yield return door;
        }

        /// <summary>
        /// Find a door by world position. UIDs drift between instances
        /// (instance-local counters), but generation-identical worlds put the
        /// same door at the same coordinates.
        /// </summary>
        public static Door FindDoorAt(Vector2 pos, float tolerance = 0.5f)
        {
            Door best = null;
            float bestSqr = tolerance * tolerance;
            foreach (var door in Doors())
            {
                float d = ((Vector2)door.tr.position - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    best = door;
                    bestSqr = d;
                }
            }
            return best;
        }

        public static IEnumerable<Item> GroundItems()
        {
            var gc = GC;
            if (gc == null)
                yield break;
            foreach (var item in gc.itemList)
                if (item != null && item.gameObject != null && item.gameObject.activeSelf)
                    yield return item;
        }

        /// <summary>Ground item by position (+name check) — same drift rationale as FindDoorAt.</summary>
        public static Item FindGroundItemAt(Vector2 pos, string itemName, float tolerance = 0.75f)
        {
            Item best = null;
            float bestSqr = tolerance * tolerance;
            foreach (var item in GroundItems())
            {
                if (itemName != null && item.invItem != null
                    && !string.Equals(item.invItem.invItemName, itemName, StringComparison.OrdinalIgnoreCase))
                    continue;
                float d = ((Vector2)item.tr.position - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    best = item;
                    bestSqr = d;
                }
            }
            return best;
        }

        public static Item SpawnGroundItem(Vector2 pos, string itemName)
        {
            var gc = GC;
            if (gc == null || gc.spawnerMain == null)
                throw new InvalidOperationException("no game running");
            return gc.spawnerMain.SpawnItem(new Vector3(pos.x, pos.y, 0f), itemName);
        }

        /// <summary>Drive a vanilla pickup: the agent interacts with the ground item.</summary>
        public static void PickUpGroundItem(int agentUid, Vector2 pos, string itemName)
        {
            var agent = Require(agentUid);
            var item = FindGroundItemAt(pos, itemName);
            if (item == null)
                throw new ArgumentException($"no ground item '{itemName}' near ({pos.x:0.#},{pos.y:0.#})");
            item.Interact(agent);
        }

        public static Door FindDoor(int uid)
        {
            var gc = GC;
            if (gc == null)
                return null;
            foreach (var objectReal in gc.objectRealList)
                if (objectReal is Door door && door.UID == uid)
                    return door;
            return null;
        }

        public static IEnumerable<ObjectReal> Objects()
        {
            var gc = GC;
            if (gc == null)
                yield break;
            foreach (var objectReal in gc.objectRealList)
                if (objectReal != null)
                    yield return objectReal;
        }

        /// <summary>Object by position + name — same UID-drift rationale as FindDoorAt.</summary>
        public static ObjectReal FindObjectAt(Vector2 pos, string objectName, float tolerance = 0.5f)
        {
            ObjectReal best = null;
            float bestSqr = tolerance * tolerance;
            foreach (var obj in Objects())
            {
                if (objectName != null && obj.objectName != objectName)
                    continue;
                if (obj.tr == null)
                    continue;
                float d = ((Vector2)obj.tr.position - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    best = obj;
                    bestSqr = d;
                }
            }
            return best;
        }

        public static ObjectReal FindObjectReal(int uid)
        {
            foreach (var obj in Objects())
                if (obj.UID == uid)
                    return obj;
            return null;
        }

        public static void DestroyObject(int uid)
        {
            var obj = FindObjectReal(uid);
            if (obj == null)
                throw new ArgumentException($"no object with uid {uid}");
            obj.DestroyMe(null);
        }

        /// <summary>Take an item from a shopkeeper-style agent inventory via
        /// vanilla's wire entry (TakeItemFromShop no-ops outside Mirror; the
        /// ECS hook publishes from it).</summary>
        public static void ShopTake(int uid, string itemName)
        {
            var seller = Require(uid);
            InvItem item = null;
            foreach (var it in seller.inventory.InvItemList)
                if (it != null && it.invItemName == itemName)
                    item = it;
            if (item == null)
                throw new ArgumentException($"agent {uid} has no '{itemName}'");
            GC.playerAgent.objectMult.TakeItemFromShop(seller, itemName, specialDatabase: false);
            seller.inventory.DestroyItem(item);
        }

        /// <summary>Objects with their own inventory (shelves, chests, safes...).</summary>
        public static IEnumerable<ObjectReal> Containers()
        {
            foreach (var obj in Objects())
                if (obj.objectInvDatabase != null && obj.tr != null)
                    yield return obj;
        }

        public static ObjectReal FindContainerAt(Vector2 pos, float tolerance = 0.75f)
        {
            ObjectReal best = null;
            float bestSqr = tolerance * tolerance;
            foreach (var c in Containers())
            {
                float d = ((Vector2)c.tr.position - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    best = c;
                    bestSqr = d;
                }
            }
            return best;
        }

        public static void ChestGive(Vector2 pos, string itemName)
        {
            var chest = FindContainerAt(pos);
            if (chest == null)
                throw new ArgumentException($"no container near ({pos.x:0.#},{pos.y:0.#})");
            if (chest.objectInvDatabase.AddItem(itemName, 1) == null)
                throw new ArgumentException($"item '{itemName}' not created");
        }

        /// <summary>Take an item out of a container through the same wire
        /// entry vanilla uses (ObjectMult.TakeItemFromChest — a no-op body in
        /// solo, but the ECS hook publishes from it).</summary>
        public static void ChestTake(Vector2 pos, string itemName)
        {
            var chest = FindContainerAt(pos);
            if (chest == null)
                throw new ArgumentException($"no container near ({pos.x:0.#},{pos.y:0.#})");
            var item = chest.objectInvDatabase.FindItem(itemName);
            if (item == null)
                throw new ArgumentException($"container has no '{itemName}'");
            var gc = GC;
            gc.playerAgent.objectMult.TakeItemFromChest(chest, itemName);
            chest.objectInvDatabase.DestroyItem(item);
        }

        public static Gas FindGasAt(Vector2 pos, float tolerance = 0.6f)
        {
            var gc = GC;
            if (gc == null)
                return null;
            Gas best = null;
            float bestSqr = tolerance * tolerance;
            foreach (var gas in gc.gasesList)
            {
                if (gas == null || gas.tr == null || gas.destroying)
                    continue;
                float d = ((Vector2)gas.tr.position - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    best = gas;
                    bestSqr = d;
                }
            }
            return best;
        }

        /// <summary>Spawn a gas cloud from a source object via the vanilla
        /// SpawnGas master (contents e.g. Flammable, Poison, Confusion).</summary>
        public static Gas SpawnGas(ObjectReal source, Vector2 pos, string contents)
        {
            var gc = GC;
            if (gc == null || gc.spawnerMain == null || source == null)
                throw new InvalidOperationException("no game/source");
            return gc.spawnerMain.SpawnGas(source, new Vector3(pos.x, pos.y, 0f),
                new List<string> { contents }, null, spawnOnClients: true);
        }

        public static IEnumerable<Fire> Fires()
        {
            var gc = GC;
            if (gc == null)
                yield break;
            foreach (var fire in gc.firesList)
                if (fire != null)
                    yield return fire;
        }

        public static Fire FindFireAt(Vector2 pos, float tolerance = 0.35f)
        {
            Fire best = null;
            float bestSqr = tolerance * tolerance;
            foreach (var fire in Fires())
            {
                if (fire.tr == null || fire.destroying)
                    continue;
                float d = ((Vector2)fire.tr.position - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    best = fire;
                    bestSqr = d;
                }
            }
            return best;
        }

        public static Fire Ignite(Vector2 pos, bool oil = false)
        {
            var gc = GC;
            if (gc == null || gc.spawnerMain == null)
                throw new InvalidOperationException("no game running");
            return gc.spawnerMain.SpawnFire(null, new Vector3(pos.x, pos.y, 0f), oil);
        }

        public static void Extinguish(Vector2 pos)
        {
            var fire = FindFireAt(pos, 0.75f);
            if (fire == null)
                throw new ArgumentException($"no fire near ({pos.x:0.#},{pos.y:0.#})");
            fire.DestroyMe();
        }

        private static uint _worldHash;
        private static int _worldHashSeed, _worldHashLevel;

        /// <summary>Every level (re)generation must drop the cache — a heal
        /// reload regenerates DIFFERENT geometry under the SAME (seed, num) key.</summary>
        public static void InvalidateWorldHash() => _worldHash = 0;

        /// <summary>
        /// Order-independent fingerprint of the level's door geometry
        /// (doors anchor the generated structure). Two instances that built
        /// the same world from the same seed get the same hash; frame-timing
        /// RNG divergence during generation changes it. 0 = level not loaded.
        /// Cached per (seed, level).
        /// </summary>
        public static uint WorldHash()
        {
            var gc = GC;
            // loadCompleteReally: set by every load path incl. solo follow
            // reloads; plain loadComplete is Mirror-era and can stay false.
            if (gc == null || gc.loadLevel == null || gc.sessionDataBig == null || !gc.loadCompleteReally)
                return 0;
            int seed = gc.loadLevel.randomSeedNum, num = gc.sessionDataBig.curLevel;
            if (_worldHash != 0 && seed == _worldHashSeed && num == _worldHashLevel)
                return _worldHash;
            // FNV-1a over each door's quantized position+type, XOR-combined
            // so enumeration order doesn't matter.
            uint combined = 2166136261u;
            int count = 0;
            foreach (var door in Doors())
            {
                if (door.tr == null)
                    continue;
                Vector2 p = door.tr.position;
                uint h = 2166136261u;
                foreach (char c in $"{door.doorType}@{Mathf.RoundToInt(p.x * 10)}:{Mathf.RoundToInt(p.y * 10)}")
                    h = (h ^ c) * 16777619u;
                combined ^= h;
                count++;
            }
            combined = (combined ^ (uint)count) * 16777619u;
            if (combined == 0)
                combined = 1; // 0 is reserved for "not loaded"
            _worldHash = combined;
            _worldHashSeed = seed;
            _worldHashLevel = num;
            return combined;
        }

        public static void OpenDoor(int uid, Agent byAgent = null)
        {
            var door = FindDoor(uid);
            if (door == null)
                throw new ArgumentException($"no door with uid {uid}");
            door.OpenDoor(byAgent);
        }

        public static bool LockDoor(int uid, bool locked)
        {
            var door = FindDoor(uid);
            if (door == null)
                throw new ArgumentException($"no door with uid {uid}");
            if (locked) door.Lock(); else door.Unlock();
            return door.locked;
        }

        public static string Summary()
        {
            var gc = GC;
            if (gc == null)
                return "no GameController";
            var sb = new StringBuilder();
            sb.Append($"level={gc.levelType} agents={gc.agentList.Count} objects={gc.objectRealList.Count} loaded={(gc.loadCompleteReally ? "yes" : "NO")}");
            if (gc.loadLevel != null)
                sb.Append($" seed={gc.loadLevel.randomSeedNum}/{gc.loadLevel.randomSeedLetter}");
            foreach (var agent in gc.playerAgentList)
                if (agent != null)
                    sb.Append($"\n  player: {DescribeAgent(agent)}");
            return sb.ToString();
        }

        // ---- reshape the world (GM verbs) -----------------------------------

        /// <summary>Detonate an explosion at a world cell — hurts nearby agents
        /// AND blows out walls (the game's own wall-destruction path). Source is
        /// the local player so it reads as "environmental".</summary>
        public static Explosion Explode(Vector2 pos, string explosionType = "Normal")
        {
            var gc = GC;
            if (gc == null || gc.spawnerMain == null)
                throw new InvalidOperationException("no game running");
            return gc.spawnerMain.SpawnExplosion(gc.playerAgent, new Vector3(pos.x, pos.y, 0f), explosionType);
        }

        /// <summary>Spawn any object/structure prefab by name at a world cell
        /// (e.g. ExplosiveBarrel, Mine, Turret, Toilet).</summary>
        public static ObjectReal SpawnObject(string objectName, Vector2 pos)
        {
            var gc = GC;
            if (gc == null || gc.spawnerMain == null)
                throw new InvalidOperationException("no game running");
            return gc.spawnerMain.spawnObjectReal(new Vector3(pos.x, pos.y, 0f), (PlayfieldObject)null, objectName);
        }

        /// <summary>Destroy the wall tile at a world cell (direct tile path — no
        /// explosion). Rounds to the integer tile grid.</summary>
        public static void DestroyWall(Vector2 pos)
        {
            var gc = GC;
            if (gc == null || gc.tileInfo == null)
                throw new InvalidOperationException("no game running");
            gc.tileInfo.DestroyWallTileAtPosition(Mathf.Round(pos.x), Mathf.Round(pos.y),
                checkPrisonWallDown: true, lastHitByAgent: null);
        }

        /// <summary>Build a normal wall tile at a world cell. Rounds to the grid.</summary>
        public static void BuildWall(Vector2 pos)
        {
            var gc = GC;
            if (gc == null || gc.tileInfo == null)
                throw new InvalidOperationException("no game running");
            gc.tileInfo.BuildWallTileAtPosition(Mathf.Round(pos.x), Mathf.Round(pos.y), wallMaterialType.Normal);
        }

        /// <summary>Lay a floor tile at a world cell (default Normal). Used by the
        /// intentional-map builder to guarantee floor under a razed/rebuilt cell
        /// so a later wall-destroy leaves walkable ground. Rounds to the grid.</summary>
        public static void BuildFloor(Vector2 pos, floorMaterialType material = floorMaterialType.Normal)
        {
            var gc = GC;
            if (gc == null || gc.tileInfo == null)
                throw new InvalidOperationException("no game running");
            gc.tileInfo.BuildFloorTileAtPosition(Mathf.Round(pos.x), Mathf.Round(pos.y), material);
        }

        /// <summary>Spawn a free-standing gas cloud at a world cell. SpawnGas
        /// hard-casts its sourceObject to ObjectReal internally
        /// (gas.sourceObject = (ObjectReal)sourceObject), so an Agent source
        /// throws InvalidCastException — the old bug. A gas cloud therefore
        /// NEEDS a real object source: reuse a GasVent already on that cell,
        /// else spawn one there and vent from it. Contents e.g. Flammable,
        /// Poison, Confusion, Tear, Knockout.</summary>
        public static Gas GasCloud(Vector2 pos, string contents = "Poison")
        {
            var gc = GC;
            if (gc == null || gc.spawnerMain == null)
                throw new InvalidOperationException("no game running");
            var source = FindObjectAt(pos, "GasVent", 0.75f)
                ?? FindObjectAt(pos, null, 4f); // any initialized object works as a source
            if (source != null)
                return gc.spawnerMain.SpawnGas(source, new Vector3(pos.x, pos.y, 0f),
                    new List<string> { contents }, null, spawnOnClients: true);
            // Nothing initialized nearby: spawn a vent, but venting from an object
            // the same tick it spawns NREs inside SpawnGas (Start() hasn't run).
            // Defer the vent until the object reports didStart.
            var vent = SpawnObject("GasVent", pos);
            if (vent == null)
                throw new InvalidOperationException("could not spawn a GasVent source object");
            EightPlayersPlugin.Instance.StartCoroutine(VentWhenReady(vent, pos, contents));
            return null; // deferred — gas appears within ~a second
        }

        private static System.Collections.IEnumerator VentWhenReady(ObjectReal source, Vector2 pos, string contents)
        {
            for (int i = 0; i < 120 && source != null && !source.didStart; i++)
                yield return null;
            yield return new WaitForSeconds(0.25f);
            var gc = GC;
            if (gc == null || gc.spawnerMain == null || source == null)
                yield break;
            try
            {
                gc.spawnerMain.SpawnGas(source, new Vector3(pos.x, pos.y, 0f),
                    new List<string> { contents }, null, spawnOnClients: true);
            }
            catch (System.Exception e)
            {
                EightPlayersPlugin.Log.LogWarning($"deferred gascloud failed: {e.Message}");
            }
        }

        // ---- AI marker (purely cosmetic, rogue-gm#12) -------------------------

        /// <summary>Original light colors of marked agents, keyed by uid, so
        /// `off` can restore. Uids churn per level — callers re-mark after
        /// level changes (stale entries are pruned opportunistically).</summary>
        private static readonly Dictionary<int, Color> _aiMarkerOriginal = new Dictionary<int, Color>();

        /// <summary>Toggle a purely COSMETIC always-visible marker on an
        /// AI-driven agent: tint the agent's own ambient glow (the
        /// `LightRealAgent` LightSprite every non-object agent carries under
        /// its sprite) a distinctive cyan. Safe because the game's per-frame
        /// brightness pass (Agent.SetLightBrightness) only rewrites the ALPHA
        /// channel and preserves RGB — so the tint persists, follows the agent
        /// (it's a child of its sprite transform), and has zero gameplay
        /// impact: no relationships, no statuses, no AI changes.</summary>
        public static string AiMarker(int uid, bool on)
        {
            var agent = Require(uid);
            // prune entries whose agent is gone (level churn) — keeps the dict tiny
            var stale = new List<int>();
            foreach (var k in _aiMarkerOriginal.Keys)
                if (k != uid && FindAgent(k) == null)
                    stale.Add(k);
            foreach (var k in stale)
                _aiMarkerOriginal.Remove(k);
            var light = agent.lightReal;
            if (light == null || light.fancyLight == null)
                throw new InvalidOperationException(
                    $"agent {uid} has no agent light (lightingType={GC?.lightingType}) — no marker possible");
            var fl = light.fancyLight;
            if (on)
            {
                if (!_aiMarkerOriginal.ContainsKey(uid))
                    _aiMarkerOriginal[uid] = fl.Color;
                // keep the game-driven alpha (floor it so the glow is visible
                // even on agents whose brightness pass isn't running)
                float alpha = Mathf.Max(fl.Color.a, 96f / 255f);
                fl.Color = new Color(0.2f, 1f, 1f, alpha);
                fl.mustForceUpdate = true; // LightSprite only re-meshes on a change signal
                if (!light.gameObject.activeSelf)
                    light.gameObject.SetActive(true);
                return $"aimarker ON: agent {uid} glow tinted cyan (cosmetic only; uids churn per level — re-mark after level changes)";
            }
            Color restored = _aiMarkerOriginal.TryGetValue(uid, out var orig) ? orig : Color.white;
            _aiMarkerOriginal.Remove(uid);
            fl.Color = new Color(restored.r, restored.g, restored.b, fl.Color.a); // alpha stays game-driven
            fl.mustForceUpdate = true;
            return $"aimarker OFF: agent {uid} glow restored";
        }

        // ---- goal injection (rogue-gm#3 / missing.md §7-4a) --------------------

        /// <summary>Enable/disable an agent's native brain, with the same
        /// active-list surgery the game's streaming activation does (the
        /// NpcSync.SetBrain pattern). While inactive the brain neither decides
        /// nor processes goals — but PathfindingAI still honors
        /// SetFinalDestPosition, so a Lua behavior's moveToward keeps working:
        /// a behavior can take full ownership of movement.</summary>
        /// <summary>Register an agent's brain into the active-brain lists and
        /// flip its active flag on — idempotent. Used to make freshly spawned
        /// NPCs live immediately (StartBrain otherwise activates over a
        /// coroutine, leaving a same-frame read false).</summary>
        public static void ActivateBrain(Agent agent)
        {
            var gc = GC;
            if (agent == null || agent.brain == null || gc == null)
                return;
            agent.brain.active = true;
            if (!gc.activeBrainAgentListIDs.Contains(agent.agentID))
            {
                gc.activeBrainAgentListIDs.Add(agent.agentID);
                gc.activeBrainAgentList.Add(agent);
            }
        }

        private static System.Collections.IEnumerator ReassertActive(Agent agent)
        {
            for (int i = 0; i < 30 && agent != null && !agent.dead; i++)
            {
                ActivateBrain(agent);
                yield return null;
            }
        }

        // ---- hard position pin (staged set-pieces) ---------------------------
        private static readonly System.Collections.Generic.Dictionary<int, Vector2> Pins
            = new System.Collections.Generic.Dictionary<int, Vector2>();

        /// <summary>Freeze an agent at a world point: a per-frame lock (PinTick)
        /// re-sets its position, winning over native AI AND tool-driven moves.
        /// Also sleeps the brain so it stops fighting. Unpin to release.</summary>
        public static void Pin(int uid, Vector2 pos)
        {
            var agent = Require(uid);
            // NEVER pin a player — the per-frame position-lock would freeze the
            // human's own controls. Pin is a set-piece freeze for NPC bodies.
            if (agent.isPlayer > 0)
                throw new ArgumentException($"agent {uid} is a PLAYER — refusing to pin (it would freeze their controls); pin is for NPC set-pieces only");
            Pins[uid] = pos;
            if (agent.tr != null) agent.tr.position = new Vector3(pos.x, pos.y, agent.tr.position.z);
            try { SetBrainActive(uid, false); } catch { /* brainless — the lock alone holds it */ }
        }

        public static void Unpin(int uid)
        {
            Pins.Remove(uid);
            try { SetBrainActive(uid, true); } catch { }
        }

        public static void UnpinAll()
        {
            foreach (var uid in new System.Collections.Generic.List<int>(Pins.Keys))
                try { SetBrainActive(uid, true); } catch { }
            Pins.Clear();
        }

        /// <summary>Called every frame from Plugin.Update — hold pinned agents
        /// at their points and drop any that died/despawned.</summary>
        public static void PinTick()
        {
            if (Pins.Count == 0) return;
            System.Collections.Generic.List<int> drop = null;
            foreach (var kv in Pins)
            {
                var a = FindAgent(kv.Key);
                if (a == null || a.dead || a.tr == null)
                {
                    (drop ?? (drop = new System.Collections.Generic.List<int>())).Add(kv.Key);
                    continue;
                }
                a.tr.position = new Vector3(kv.Value.x, kv.Value.y, a.tr.position.z);
                if (a.rb != null) a.rb.velocity = Vector2.zero;
            }
            if (drop != null) foreach (var uid in drop) Pins.Remove(uid);
        }

        public static bool SetBrainActive(int uid, bool active)
        {
            var agent = Require(uid);
            var gc = GC;
            if (agent.brain == null || gc == null)
                throw new InvalidOperationException($"agent {uid} has no brain");
            if (agent.brain.active == active)
                return active;
            agent.brain.active = active;
            if (active)
            {
                if (!gc.activeBrainAgentListIDs.Contains(agent.agentID))
                {
                    gc.activeBrainAgentListIDs.Add(agent.agentID);
                    gc.activeBrainAgentList.Add(agent);
                }
            }
            else
            {
                gc.activeBrainAgentListIDs.Remove(agent.agentID);
                gc.activeBrainAgentList.Remove(agent);
            }
            return active;
        }

        /// <summary>Inject a REAL goal into an NPC's brain, riding the game's
        /// own goal system — so a directed NPC actually walks over and DOES
        /// the thing (missing.md §7-4a: `walk_npc` gets re-routed, `say` is
        /// theater). Three moving parts, all vanilla:
        ///  1. construct the concrete Goal subclass BrainUpdate's decision
        ///     loop itself builds (GoalFollow/GoalGuard/... with its target
        ///     fields set — a bare AddSubgoal2 Goal has no behavior);
        ///  2. set the BACKING agent state the decision loop reads
        ///     (following / defaultGoalCode / investigation), else the very
        ///     next think tick re-decides the goal away;
        ///  3. install it via BrainUpdate.SwitchGoal — the game's own
        ///     goal-change entry point (terminates the current goal, wires
        ///     gc/agent/brain, sets mostRecentGoal).</summary>
        public static string SetGoal(int uid, string goalName, int targetUid = 0, Vector2? pos = null)
        {
            var agent = Require(uid);
            if (agent.brain == null || agent.brainUpdate == null)
                throw new InvalidOperationException($"agent {uid} has no brain/brainUpdate");
            Agent target = targetUid != 0 ? Require(targetUid) : null;
            Goal goal;
            string detail;
            switch (CanonicalGoal(goalName))
            {
                case "Follow":
                {
                    if (target == null)
                        throw new ArgumentException("Follow needs a target: setgoal <uid> Follow <targetUid|player>");
                    agent.SetFollowing(target); // backing state — the brain keeps re-choosing Follow
                    goal = new GoalFollow { followingAgent = target };
                    detail = $"following agent {target.UID}";
                    break;
                }
                case "Guard":
                {
                    // Guard paths back to startingPosition — point that at the
                    // given spot (or the target's / the agent's own position).
                    Vector2 guardPos = pos ?? (target != null ? (Vector2)target.tr.position : (Vector2)agent.tr.position);
                    agent.startingPosition = guardPos;
                    SetDefaultGoalRaw(agent, "Guard"); // decision loop picks Guard from defaultGoalCode
                    goal = new GoalGuard();
                    detail = $"guarding ({guardPos.x:0.#},{guardPos.y:0.#})";
                    break;
                }
                case "Battle":
                {
                    if (target == null)
                        throw new ArgumentException("Battle needs a target: setgoal <uid> Battle <targetUid|player>");
                    goal = new GoalBattle { battlingAgent = target };
                    detail = $"battling agent {target.UID} (persists while the brain still wants the fight — pair with `status <uid> Enraged` for a lasting grudge)";
                    break;
                }
                case "Flee":
                {
                    if (target == null)
                        throw new ArgumentException("Flee needs a target: setgoal <uid> Flee <targetUid|player>");
                    goal = new GoalFlee { fleeingAgent = target };
                    detail = $"fleeing agent {target.UID}";
                    break;
                }
                case "Investigate":
                {
                    if (pos == null && target == null)
                        throw new ArgumentException("Investigate needs a position or target: setgoal <uid> Investigate <x,y | targetUid>");
                    Vector2 p = pos ?? (Vector2)target.tr.position;
                    agent.investigation = 1; // decision loop keeps Investigate while > 0 (and 1 = walk there + search)
                    agent.investigatePosition = new Vector3(p.x, p.y, 0f);
                    goal = new GoalInvestigate { investigatePosition = new Vector3(p.x, p.y, 0f) };
                    detail = $"investigating ({p.x:0.#},{p.y:0.#})";
                    break;
                }
                case "Wander":
                    SetDefaultGoalRaw(agent, "Wander");
                    goal = new GoalWander();
                    detail = "wandering";
                    break;
                case "WanderFar":
                    SetDefaultGoalRaw(agent, "WanderFar");
                    goal = new GoalWanderFar();
                    detail = "wandering far";
                    break;
                default:
                    throw new ArgumentException(
                        $"unsupported goal '{goalName}' — supported: Follow <target>, Guard [x,y|target], Battle <target>, Flee <target>, Investigate <x,y|target>, Wander, WanderFar");
            }
            if (agent.brain.Goals.Count == 0)
                agent.brain.RecycleStart(); // never-started brain: seed Goals[0] so SwitchGoal has something to replace
            agent.brainUpdate.SwitchGoal(goal);
            // A goal is worthless if the brain isn't ticking. Force-activate —
            // spawned NPCs can read brain.active=false (the StartBrain coroutine
            // hasn't flipped it, or overwrote our spawn-time activation), which
            // left Battle/Follow goals sitting idle. This runs at command time,
            // after spawn has settled, so it holds.
            SetBrainActive(uid, true);
            return $"agent {uid} goal={goal.goalName}: {detail} (brain active)";
        }

        private static readonly string[] SupportedGoals =
            { "Follow", "Guard", "Battle", "Flee", "Investigate", "Wander", "WanderFar" };

        private static string CanonicalGoal(string goalName)
        {
            foreach (var g in SupportedGoals)
                if (string.Equals(g, goalName, StringComparison.OrdinalIgnoreCase))
                    return g;
            return goalName; // falls through to the error in SetGoal
        }

        /// <summary>agent.SetDefaultGoal early-returns when gc.serverPlayer is
        /// false — set the same fields it sets, directly.</summary>
        private static void SetDefaultGoalRaw(Agent agent, string goalName)
        {
            agent.defaultGoal = goalName;
            agent.defaultGoalCode = agent.GetGoalCode(goalName);
            if (agent.oma != null)
                agent.oma.defaultGoalCode = (int)agent.defaultGoalCode;
        }

        /// <summary>Recruit an NPC into the local player's party through the
        /// vanilla JoinParty choke point ("that Cop works for you now").</summary>
        public static void Recruit(int uid)
        {
            var agent = Require(uid);
            var gc = GC;
            if (gc == null || gc.playerAgent == null)
                throw new InvalidOperationException("no player");
            agent.relationships.JoinParty(gc.playerAgent);
        }

        // ---- one-shot GM readouts (JSON) --------------------------------------

        /// <summary>One-shot inventory listing — JSON array of
        /// {name, count, equipped} from the agent's InvItemList. invItemRealName
        /// is the canonical item id (invItemName is often null). Kills the
        /// 25-round-trip indexed item reads.</summary>
        public static string InventoryJson(int uid)
        {
            var agent = Require(uid);
            var arr = new JArray();
            foreach (var it in agent.inventory.InvItemList)
            {
                if (it == null || string.IsNullOrEmpty(it.invItemRealName))
                    continue;
                arr.Add(new JObject
                {
                    ["name"] = it.invItemRealName,
                    ["count"] = it.invItemCount,
                    ["equipped"] = it.equipped,
                });
            }
            return arr.ToString(Formatting.None);
        }

        /// <summary>Agents AND objects within radius of a point, sorted by
        /// distance, capped at ~40 entries total — "what's around the player"
        /// in one round trip.</summary>
        public static string NearbyJson(Vector2 pos, float radius)
        {
            var gc = GC;
            if (gc == null)
                throw new InvalidOperationException("no game running");
            float r2 = radius * radius;
            var hits = new List<(float d2, bool isAgent, JObject o)>();
            foreach (var a in Agents())
            {
                if (a.tr == null) continue;
                Vector2 p = a.tr.position;
                float d2 = (p - pos).sqrMagnitude;
                if (d2 > r2) continue;
                hits.Add((d2, true, new JObject
                {
                    ["uid"] = a.UID,
                    ["name"] = string.IsNullOrEmpty(a.agentRealName) ? a.agentName : a.agentRealName,
                    ["type"] = a.agentName,
                    ["x"] = Mathf.Round(p.x * 100f) / 100f,
                    ["y"] = Mathf.Round(p.y * 100f) / 100f,
                    ["hp"] = Mathf.Round(a.health * 10f) / 10f,
                    ["dead"] = a.dead,
                }));
            }
            foreach (var o in Objects())
            {
                if (o.tr == null || o.destroying) continue;
                Vector2 p = o.tr.position;
                float d2 = (p - pos).sqrMagnitude;
                if (d2 > r2) continue;
                hits.Add((d2, false, new JObject
                {
                    ["uid"] = o.UID,
                    ["name"] = o.objectName,
                    ["x"] = Mathf.Round(p.x * 100f) / 100f,
                    ["y"] = Mathf.Round(p.y * 100f) / 100f,
                }));
            }
            hits.Sort((a, b) => a.d2.CompareTo(b.d2));
            var agents = new JArray();
            var objects = new JArray();
            int taken = 0;
            foreach (var h in hits)
            {
                if (taken >= 40) break;
                (h.isAgent ? agents : objects).Add(h.o);
                taken++;
            }
            return new JObject
            {
                ["agents"] = agents,
                ["objects"] = objects,
                ["total"] = hits.Count,
            }.ToString(Formatting.None);
        }

        /// <summary>EXPERIMENTAL: make an NPC walk somewhere via its OWN
        /// pathfinding (the same finalDestPosition mechanism the game's goals
        /// feed PathfindingAI.UpdateTargetPosition with). The brain's active
        /// goal may re-route the agent on its next think tick, so treat this
        /// as a nudge, not a lock.</summary>
        public static void WalkNpc(int uid, Vector2 pos)
        {
            var agent = Require(uid);
            agent.SetFinalDestObject(null);
            agent.SetFinalDestPosition(new Vector3(pos.x, pos.y, 0f));
        }

        private static Agent Require(int uid)
        {
            var agent = FindAgent(uid);
            if (agent == null)
                throw new ArgumentException($"no agent with uid {uid}");
            return agent;
        }
    }
}
