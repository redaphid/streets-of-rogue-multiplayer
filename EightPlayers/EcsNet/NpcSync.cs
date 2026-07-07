using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers.EcsNet
{
    // NPC mirroring across instances. NPC spawn ORDER during level generation
    // is seed-deterministic (docs/trace-choke-points.md), so "the k-th NPC
    // spawned while the level loads" identifies the same character everywhere.
    // The lowest client id in the room is the NPC authority: it keeps its
    // brains running and publishes each generation NPC as an entity
    // ({npc:{i,type}, pos}). Followers disable the matching local NPC's brain
    // and drive it from the entity. Authority failover is free: the DO
    // despawns a leaver's entities, followers unbind (re-enabling brains),
    // and the new lowest client starts publishing.
    //
    // Scope: generation-spawned NPCs only. Dynamically spawned agents (cops
    // called mid-level etc.) are not registered and stay local.
    internal sealed class NpcSync
    {
        private const float MinMoveSqr = 0.05f * 0.05f;
        private const float PublishInterval = 0.2f;

        private sealed class Published
        {
            public int Entity = -1;
            public int Tmp = -1;
            public Vector2 LastSent;
            public bool HpDirty;
            public bool DeadDirty;
        }

        private sealed class Bound
        {
            public Agent Agent;
            public Vector2 Target;
            public float AppliedHp = float.NaN;
            public bool AppliedDead;
        }

        private readonly List<Agent> _byIndex = new List<Agent>();
        private readonly Dictionary<Agent, int> _indexByAgent = new Dictionary<Agent, int>();
        private JArray _batch;
        private readonly Dictionary<int, Published> _published = new Dictionary<int, Published>(); // npc idx ->
        private readonly Dictionary<int, Bound> _bound = new Dictionary<int, Bound>();             // entity ->
        private float _nextPublishAt;

        /// <summary>Level (re)generated: everything resets.</summary>
        public void OnLevelGenerated()
        {
            _byIndex.Clear();
            _indexByAgent.Clear();
            _published.Clear();
            UnbindAll();
        }

        public void Reset()
        {
            _byIndex.Clear();
            _indexByAgent.Clear();
            _published.Clear();
            UnbindAll();
        }

        /// <summary>Register a generation-window NPC spawn (from the SpawnAgent hook).</summary>
        public void Register(Agent agent)
        {
            _indexByAgent[agent] = _byIndex.Count;
            _byIndex.Add(agent);
        }

        /// <summary>Authority: a registered NPC's health changed (ChangeHealth hook).</summary>
        public void MarkNpcHealth(Agent agent)
        {
            if (!_indexByAgent.TryGetValue(agent, out var i))
            {
                EightPlayersPlugin.Log.LogWarning($"ECSNET npc hp: agent {agent.UID} not in registry");
                return;
            }
            if (!_published.TryGetValue(i, out var pub))
            {
                EightPlayersPlugin.Log.LogWarning($"ECSNET npc hp: index {i} not published");
                return;
            }
            pub.HpDirty = true;
        }

        /// <summary>Authority: a registered NPC died (SetupDeath hook).</summary>
        public void MarkNpcDeath(Agent agent)
        {
            if (!_indexByAgent.TryGetValue(agent, out var i))
            {
                EightPlayersPlugin.Log.LogWarning($"ECSNET npc death: agent {agent.UID} not in registry");
                return;
            }
            if (!_published.TryGetValue(i, out var pub))
            {
                EightPlayersPlugin.Log.LogWarning($"ECSNET npc death: index {i} not published");
                return;
            }
            pub.DeadDirty = true;
            EightPlayersPlugin.Log.LogInfo($"ECSNET npc death queued for index {i} (agent {agent.UID})");
        }

        public int RegisteredCount => _byIndex.Count;

        public IEnumerable<string> DescribeRegistry()
        {
            for (int i = 0; i < _byIndex.Count; i++)
            {
                var agent = _byIndex[i];
                _published.TryGetValue(i, out var pub);
                if (agent == null)
                {
                    yield return $"  npc[{i}] (gone) entity={pub?.Entity ?? -1}";
                    continue;
                }
                Vector2 p = agent.tr != null ? (Vector2)agent.tr.position : Vector2.zero;
                yield return $"  npc[{i}] uid={agent.UID} {agent.agentName} pos=({p.x:0.#},{p.y:0.#}) dead={agent.dead} entity={pub?.Entity ?? -1}";
            }
        }

        /// <summary>Forwarded spawn ack for a tmp id we own. Returns false if not ours.</summary>
        public bool OnSpawnAck(int tmp, int entity)
        {
            foreach (var pub in _published.Values)
            {
                if (pub.Tmp == tmp)
                {
                    pub.Tmp = -1;
                    pub.Entity = entity;
                    return true;
                }
            }
            return false;
        }

        // ---- authority side ----

        public void PublishTick(EcsNetManager net)
        {
            if (Time.unscaledTime < _nextPublishAt)
                return;
            _nextPublishAt = Time.unscaledTime + PublishInterval;

            for (int i = 0; i < _byIndex.Count; i++)
            {
                var agent = _byIndex[i];
                _published.TryGetValue(i, out var pub);

                if (agent == null)
                {
                    if (pub != null && pub.Entity >= 0)
                        net.SendDespawn(pub.Entity);
                    if (pub != null)
                        _published.Remove(i);
                    continue;
                }

                if (agent.dead)
                {
                    // Keep the entity as a corpse marker; publish death once.
                    if (pub != null && pub.Entity >= 0 && pub.DeadDirty)
                    {
                        net.SendDead(pub.Entity, agent.health, agent.healthMax);
                        pub.DeadDirty = false;
                        pub.HpDirty = false;
                    }
                    continue;
                }

                Vector2 p = agent.tr.position;
                if (pub == null)
                {
                    pub = new Published { Tmp = net.SendNpcSpawn(i, agent.agentName, p), LastSent = p };
                    _published[i] = pub;
                    continue;
                }
                if (pub.Entity < 0)
                    continue;
                JObject components = null;
                if ((p - pub.LastSent).sqrMagnitude > MinMoveSqr)
                {
                    components = Protocol.PosComponent(p.x, p.y);
                    pub.LastSent = p;
                }
                if (pub.HpDirty)
                {
                    var hp = Protocol.HpComponent(agent.health, agent.healthMax);
                    if (components == null) components = hp;
                    else components.Merge(hp);
                    pub.HpDirty = false;
                }
                if (components != null)
                    (_batch = _batch ?? new JArray()).Add(Protocol.BatchUpdate(pub.Entity, components));
            }
            if (_batch != null && _batch.Count > 0)
            {
                net.SendBatch(_batch);
                _batch = null;
            }
        }

        /// <summary>Lost authority (someone with a lower id appeared): retract our copies.</summary>
        public void RetractPublished(EcsNetManager net)
        {
            foreach (var pub in _published.Values)
                if (pub.Entity >= 0)
                    net.SendDespawn(pub.Entity);
            _published.Clear();
        }

        // ---- follower side ----

        public void FollowerSync(EcsWorld world, int myClientId)
        {
            world.ForEach<NpcTag>((e, tag) =>
            {
                if (world.TryGet<Owned>(e, out var owned) && owned.ClientId == myClientId)
                    return;
                if (!world.TryGet<Pos>(e, out var pos))
                    return;

                if (!_bound.TryGetValue(e, out var bound))
                {
                    if (tag.Index < 0 || tag.Index >= _byIndex.Count)
                        return;
                    var agent = _byIndex[tag.Index];
                    if (agent == null || agent.dead)
                        return;
                    SetBrain(agent, false);
                    bound = new Bound { Agent = agent };
                    _bound[e] = bound;
                }
                bound.Target = new Vector2(pos.X, pos.Y);

                // Mirror authority combat state through vanilla-adjacent paths.
                if (bound.Agent != null && !bound.Agent.dead)
                {
                    if (!bound.AppliedDead && world.TryGet<DeadTag>(e, out var dead) && dead.Value)
                    {
                        bound.AppliedDead = true;
                        try
                        {
                            bound.Agent.statusEffects.SetupDeath(null, killedOnClient: true, noSFX: false);
                        }
                        catch
                        {
                            // death FX path can throw during teardown; state still applied below
                        }
                        bound.Agent.health = 0f;
                    }
                    else if (world.TryGet<Hp>(e, out var hp) && !float.IsNaN(hp.Cur) && hp.Cur != bound.AppliedHp)
                    {
                        bound.AppliedHp = hp.Cur;
                        bound.Agent.health = hp.Cur;
                    }
                }
            });

            List<int> gone = null;
            foreach (var e in _bound.Keys)
                if (!world.Exists(e))
                    (gone = gone ?? new List<int>()).Add(e);
            if (gone != null)
                foreach (var e in gone)
                    Unbind(e);
        }

        public void Drive()
        {
            foreach (var bound in _bound.Values)
            {
                var agent = bound.Agent;
                if (agent == null || agent.tr == null || agent.dead)
                    continue;
                Vector2 current = agent.tr.position;
                var deltaSqr = (bound.Target - current).sqrMagnitude;
                if (deltaSqr > 9f)
                    agent.movement.Teleport(bound.Target);
                else if (deltaSqr > 0.0004f)
                {
                    var next = Vector2.Lerp(current, bound.Target, 10f * Time.deltaTime);
                    agent.tr.position = new Vector3(next.x, next.y, agent.tr.position.z);
                }
            }
        }

        public void UnbindAll()
        {
            foreach (var e in new List<int>(_bound.Keys))
                Unbind(e);
        }

        private void Unbind(int e)
        {
            if (!_bound.TryGetValue(e, out var bound))
                return;
            _bound.Remove(e);
            if (bound.Agent != null && !bound.Agent.dead)
                SetBrain(bound.Agent, true);
        }

        // Mirrors the game's own streaming activation/deactivation list surgery.
        private static void SetBrain(Agent agent, bool active)
        {
            var gc = GameController.gameController;
            if (agent.brain == null || gc == null || agent.brain.active == active)
                return;
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
        }
    }
}
