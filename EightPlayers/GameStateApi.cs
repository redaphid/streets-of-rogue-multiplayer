using System;
using System.Collections.Generic;
using System.Text;
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
            return gc.spawnerMain.SpawnAgent(new Vector3(pos.x, pos.y, 0f), agentType, playerColor);
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

        public static void GiveItem(int uid, string itemName, int count)
        {
            var agent = Require(uid);
            var item = agent.inventory.AddItem(itemName, count);
            if (item == null)
                throw new ArgumentException($"item '{itemName}' not created");
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

        public static void OpenDoor(int uid, Agent byAgent = null)
        {
            var door = FindDoor(uid);
            if (door == null)
                throw new ArgumentException($"no door with uid {uid}");
            door.OpenDoor(byAgent);
        }

        public static string Summary()
        {
            var gc = GC;
            if (gc == null)
                return "no GameController";
            var sb = new StringBuilder();
            sb.Append($"level={gc.levelType} agents={gc.agentList.Count} objects={gc.objectRealList.Count}");
            if (gc.loadLevel != null)
                sb.Append($" seed={gc.loadLevel.randomSeedNum}/{gc.loadLevel.randomSeedLetter}");
            foreach (var agent in gc.playerAgentList)
                if (agent != null)
                    sb.Append($"\n  player: {DescribeAgent(agent)}");
            return sb.ToString();
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
