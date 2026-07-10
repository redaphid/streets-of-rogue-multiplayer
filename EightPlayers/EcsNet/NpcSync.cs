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

        /// <summary>Dynamic (post-load) NPCs live in their own index region.</summary>
        public const int DynBase = 10000;

        /// <summary>
        /// Scope flag: set while WE spawn agents deliberately (avatars, remote
        /// applies, command-channel verbs) so the follower-side suppression of
        /// game-initiated dynamic spawns doesn't eat our own calls.
        /// </summary>
        public static bool BypassSuppression;

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
        private readonly List<Agent> _dynAgents = new List<Agent>();
        private readonly Dictionary<Agent, int> _indexByAgent = new Dictionary<Agent, int>();
        private readonly Dictionary<int, float> _mirrorRetryAt = new Dictionary<int, float>();   // entity ->
        private readonly Dictionary<int, int> _mirrorAttempts = new Dictionary<int, int>();      // entity ->
        private const int MaxMirrorAttempts = 5;
        private const float MirrorRetrySeconds = 10f;
        private JArray _batch;
        private readonly Dictionary<int, Published> _published = new Dictionary<int, Published>(); // npc idx ->
        private readonly Dictionary<int, Bound> _bound = new Dictionary<int, Bound>();             // entity ->
        private float _nextPublishAt;

        /// <summary>Level (re)generated: everything resets.</summary>
        public void OnLevelGenerated()
        {
            _byIndex.Clear();
            _dynAgents.Clear();
            _indexByAgent.Clear();
            _published.Clear();
            _mirrorRetryAt.Clear();
            _mirrorAttempts.Clear();
            UnbindAll();
        }

        public void Reset()
        {
            _byIndex.Clear();
            _dynAgents.Clear();
            _indexByAgent.Clear();
            _published.Clear();
            _mirrorRetryAt.Clear();
            _mirrorAttempts.Clear();
            UnbindAll();
        }

        /// <summary>Register a generation-window NPC spawn (from the SpawnAgent hook).</summary>
        public void Register(Agent agent)
        {
            _indexByAgent[agent] = _byIndex.Count;
            _byIndex.Add(agent);
        }

        /// <summary>Drift-immune NPC addressing for events (registries are
        /// spawn-order aligned on every client).</summary>
        public int IndexFor(Agent agent) =>
            agent != null && _indexByAgent.TryGetValue(agent, out var i) ? i : -1;

        public Agent AgentAt(int index) =>
            index >= 0 && index < _byIndex.Count ? _byIndex[index] : null;

        /// <summary>Authority only: register a post-load (dynamic) NPC spawn.</summary>
        public void RegisterDynamic(Agent agent)
        {
            _indexByAgent[agent] = DynBase + _dynAgents.Count;
            _dynAgents.Add(agent);
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
                PublishOne(net, _byIndex[i], i);
            for (int k = 0; k < _dynAgents.Count; k++)
                PublishOne(net, _dynAgents[k], DynBase + k);

            if (_batch != null && _batch.Count > 0)
            {
                net.SendBatch(_batch);
                _batch = null;
            }
        }

        private void PublishOne(EcsNetManager net, Agent agent, int i)
        {
            _published.TryGetValue(i, out var pub);

            if (agent == null)
            {
                if (pub != null && pub.Entity >= 0)
                    net.SendDespawn(pub.Entity);
                if (pub != null)
                    _published.Remove(i);
                return;
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
                return;
            }

            Vector2 p = agent.tr.position;
            if (pub == null)
            {
                pub = new Published { Tmp = net.SendNpcSpawn(i, agent.agentName, p), LastSent = p };
                _published[i] = pub;
                return;
            }
            if (pub.Entity < 0)
                return;
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
                    Agent agent;
                    if (tag.Index >= DynBase)
                    {
                        // Dynamic NPC: no local twin exists — spawn a mirror.
                        // Bounded retries: spawns into not-yet-streamed chunks
                        // can die instantly, so back off instead of thrashing.
                        if (world.TryGet<DeadTag>(e, out var alreadyDead) && alreadyDead.Value)
                            return;
                        _mirrorAttempts.TryGetValue(e, out var attempts);
                        if (attempts >= MaxMirrorAttempts)
                            return;
                        if (_mirrorRetryAt.TryGetValue(e, out var retryAt) && Time.unscaledTime < retryAt)
                            return;
                        _mirrorAttempts[e] = attempts + 1;
                        _mirrorRetryAt[e] = Time.unscaledTime + MirrorRetrySeconds;
                        try
                        {
                            BypassSuppression = true;
                            agent = GameStateApi.SpawnAgent(
                                string.IsNullOrEmpty(tag.Type) ? "Hobo" : tag.Type,
                                new Vector2(pos.X, pos.Y));
                        }
                        catch
                        {
                            return; // level not ready; retry after cooldown
                        }
                        finally
                        {
                            BypassSuppression = false;
                        }
                        if (agent == null)
                            return;
                        if (agent.dead)
                        {
                            EightPlayersPlugin.Log.LogWarning(
                                $"ECSNET dynamic mirror for entity {e} ({tag.Type}) spawned dead at ({pos.X:0.#},{pos.Y:0.#}) — attempt {attempts + 1}/{MaxMirrorAttempts}");
                            return;
                        }
                        EightPlayersPlugin.Log.LogInfo(
                            $"ECSNET dynamic npc mirror spawned ({tag.Type}, entity {e}, attempt {attempts + 1})");
                    }
                    else
                    {
                        if (tag.Index < 0 || tag.Index >= _byIndex.Count)
                            return;
                        agent = _byIndex[tag.Index];
                    }
                    if (agent == null || agent.dead)
                        return;
                    SetBrain(agent, false);
                    bound = new Bound { Agent = agent };
                    _bound[e] = bound;
                }
                bound.Target = new Vector2(pos.X, pos.Y);

                // A mirror that died locally (hazard/culling) without an
                // authority death: unbind so the bounded retry logic can
                // replace it, and log the event for diagnosis.
                if (tag.Index >= DynBase && bound.Agent != null && bound.Agent.dead && !bound.AppliedDead)
                {
                    EightPlayersPlugin.Log.LogWarning($"ECSNET dynamic mirror for entity {e} died locally; will re-mirror");
                    Unbind(e);
                    return;
                }

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
