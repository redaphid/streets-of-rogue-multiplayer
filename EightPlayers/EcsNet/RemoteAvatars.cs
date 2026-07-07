using System.Collections.Generic;
using UnityEngine;

namespace EightPlayers.EcsNet
{
    // Real in-world bodies for remote players: each remote same-world player
    // entity gets an actual Agent (spawned through the vanilla SpawnerMain
    // choke point, brain disabled the same way the game's own streaming
    // culling does it) driven toward the entity's pos component. Removal goes
    // through the vanilla death path (noSFX) since agents have no despawn API
    // outside level resets.
    internal sealed class RemoteAvatars
    {
        private sealed class Avatar
        {
            public Agent Agent;
            public Vector2 Target;
            public string Name;
            public bool AppliedDead;
        }

        private readonly Dictionary<int, Avatar> _avatars = new Dictionary<int, Avatar>();

        /// <summary>Reverse lookup: is this agent one of our avatars, and for which entity?</summary>
        public bool TryGetEntityFor(Agent agent, out int entity)
        {
            foreach (var kv in _avatars)
            {
                if (ReferenceEquals(kv.Value.Agent, agent))
                {
                    entity = kv.Key;
                    return true;
                }
            }
            entity = -1;
            return false;
        }

        public void Sync(EcsWorld world, int myClientId, LevelId here)
        {
            world.ForEach<PlayerInfo>((e, info) =>
            {
                if (world.TryGet<Owned>(e, out var owned) && owned.ClientId == myClientId)
                    return;
                if (!world.TryGet<Pos>(e, out var pos))
                    return;

                var sameWorld = here.Seed != 0
                    && world.TryGet<LevelId>(e, out var lvl)
                    && lvl.Seed == here.Seed && lvl.Num == here.Num;

                _avatars.TryGetValue(e, out var avatar);
                if (!sameWorld)
                {
                    if (avatar != null)
                        Remove(e);
                    return;
                }

                // The player this avatar represents is dead: kill the avatar
                // through the vanilla path and keep the corpse (no respawn).
                if (world.TryGet<DeadTag>(e, out var dead) && dead.Value)
                {
                    if (avatar != null && avatar.Agent != null && !avatar.AppliedDead)
                    {
                        avatar.AppliedDead = true;
                        if (!avatar.Agent.dead)
                        {
                            try
                            {
                                avatar.Agent.statusEffects.SetupDeath(null, killedOnClient: true, noSFX: false);
                            }
                            catch
                            {
                                // teardown race; corpse state is what matters
                            }
                        }
                    }
                    return;
                }

                if (avatar == null || avatar.Agent == null || avatar.Agent.dead)
                {
                    if (avatar != null)
                        _avatars.Remove(e);
                    var type = string.IsNullOrEmpty(info.Char) ? "Hobo" : info.Char;
                    Agent spawned;
                    try
                    {
                        spawned = GameStateApi.SpawnAgent(type, new Vector2(pos.X, pos.Y));
                    }
                    catch
                    {
                        return; // level not ready yet; retry next tick
                    }
                    if (spawned == null)
                        return;
                    Neutralize(spawned, info);
                    avatar = new Avatar { Agent = spawned, Name = info.Name };
                    _avatars[e] = avatar;
                    EightPlayersPlugin.Log.LogInfo($"ECSNET avatar spawned for entity {e} ({info.Name} as {type})");
                }

                avatar.Target = new Vector2(pos.X, pos.Y);
            });

            List<int> gone = null;
            foreach (var e in _avatars.Keys)
                if (!world.Exists(e))
                    (gone = gone ?? new List<int>()).Add(e);
            if (gone != null)
                foreach (var e in gone)
                    Remove(e);
        }

        /// <summary>Move avatars toward their targets. Call every frame.</summary>
        public void Drive()
        {
            foreach (var avatar in _avatars.Values)
            {
                var agent = avatar.Agent;
                if (agent == null || agent.tr == null)
                    continue;
                // SetupAgent's async init overwrites agentRealName; keep re-asserting ours.
                if (!string.IsNullOrEmpty(avatar.Name) && agent.agentRealName != avatar.Name)
                    agent.agentRealName = avatar.Name;
                Vector2 current = agent.tr.position;
                var distance = (avatar.Target - current).magnitude;
                if (distance > 3f)
                    agent.movement.Teleport(avatar.Target);
                else if (distance > 0.02f)
                {
                    var next = Vector2.Lerp(current, avatar.Target, 12f * Time.deltaTime);
                    agent.tr.position = new Vector3(next.x, next.y, agent.tr.position.z);
                }
            }
        }

        public void RemoveEntity(int e) => Remove(e);

        public void Clear()
        {
            foreach (var e in new List<int>(_avatars.Keys))
                Remove(e);
        }

        private void Remove(int e)
        {
            if (!_avatars.TryGetValue(e, out var avatar))
                return;
            _avatars.Remove(e);
            var agent = avatar.Agent;
            if (agent != null && !agent.dead)
            {
                try
                {
                    agent.statusEffects.SetupDeath(null, killedOnClient: false, noSFX: true);
                }
                catch
                {
                    // level tearing down; pool reset will reclaim it
                }
            }
        }

        // Same moves the game's own streaming culling makes when it parks an
        // agent's AI (Agent.cs ~10470).
        private static void Neutralize(Agent agent, PlayerInfo info)
        {
            var gc = GameController.gameController;
            if (agent.brain != null && agent.brain.active)
            {
                agent.brain.active = false;
                gc.activeBrainAgentListIDs.Remove(agent.agentID);
                gc.activeBrainAgentList.Remove(agent);
            }
            if (!string.IsNullOrEmpty(info.Name))
                agent.agentRealName = info.Name;
        }
    }
}
