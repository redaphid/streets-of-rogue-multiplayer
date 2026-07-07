# Vanilla state-mutation choke points (trace targets)

Survey of the decompiled game (2026-07-07) for the ECS migration. Each entry
is the master method every thinner overload funnels into — patch these, not
the overloads. Instrumentation lives in `EightPlayers/Tracing/GamePatches.cs`;
enable with `SOR_TRACE=1` → `traces/trace-*.jsonl` next to the game.

| System | Choke point | Notes |
|---|---|---|
| Agent spawn | `SpawnerMain.SpawnAgent(...)` 14-arg master (`SpawnerMain.cs:1658`) | All ~12 overloads funnel here. Non-server clients reroute to `ObjectMult.CmdSpawnAgent` (`ObjectMult.cs:7794`). |
| Health change | `StatusEffects.ChangeHealth(float, PlayfieldObject, uint, float, string, byte)` (`StatusEffects.cs:257`) | The single HP mutation point for agents and objects. `Agent.health` field. |
| Death | `StatusEffects.SetupDeath(PlayfieldObject, bool, bool)` (`StatusEffects.cs:2110`) | Sets `agent.dead`; multiplayer routes via `ObjectMult.CmdSetupDeath`. |
| Damage entry | `Agent.Damage(PlayfieldObject, bool)` (`Agent.cs:7253`), base `PlayfieldObject.Damage` (`PlayfieldObject.cs:1571`) | Computes damage then calls ChangeHealth — trace ChangeHealth instead. |
| Inventory add | `InvDatabase.AddItem(...)` 21-arg master (`InvDatabase.cs:5621`) | All AddItem/AddItemOrDrop overloads funnel here. `InvDatabase.agent` is the owner. |
| Inventory drop | `InvDatabase.DropItem(InvItem, bool, Vector2, bool)` (`InvDatabase.cs:2730`) | Pickup entry: `InvDatabase.PickUpItem()` (`:1561`). |
| Door open | `Door.OpenDoor(Agent, bool)` (`Door.cs:1726`) | Lock/Unlock at `Door.cs:1537/1588`. General interaction dispatch is virtual (`PlayfieldObject.Interact`), overridden everywhere — patch concrete verbs instead. |
| Level seed | `LoadLevel.randomSeedNum/randomSeedLetter` (`LoadLevel.cs:64/66`); `UnityEngine.Random.InitState(randomSeedNum)` at `LoadLevel.cs:797` | Seed resolved in `loadStuff2()`; we trace at `SetupBasicLevel()` (`:2452`), after resolution. |
| Player movement | `Movement.PlayerMovement()` (`Movement.cs:2164`), called from `Agent.AgentFixedUpdate` (`Agent.cs:13019`) | The one place local input becomes force on the body. Traced sampled (every 25th step). NPC motion is separate: `Movement.MoveNormal` (`:140`). |

## The sync protocol itself

The game has **no [SyncVar]s on Agent** — all state replication is manual
through Mirror Commands/RPCs on the `ObjectMult*` hubs:

- `ObjectMult`: 212 `[Command]`, 198 `[ClientRpc]`, 15 `[TargetRpc]`, 31 `[SyncVar]`
- Plus `ObjectMultAgent`, `ObjectMultItem`, `ObjectMultObject`,
  `ObjectMultPlayfield`, `ObjectMultFire`.

Mirror's weaver splits each into `UserCode_<name>__<argtypes>` (the body) and
`InvokeUserCode_*` (dispatch trampoline). `NetTrace.Install` reflectively
prefixes **every UserCode_ body** (460 at last run, 0 failures) and emits
`net/call` events — the complete vanilla wire behavior, which is exactly the
contract each ECS system port must reproduce.

`Mirror/GeneratedNetworkCode.cs` is only reader/writer plumbing — not a
behavior target.

## Trace event vocabulary

One JSON object per line: `{ts, cat, ev, ...}` where `ts` is
`Time.unscaledTime` (main-thread sampled).

- `trace/start`, `trace/dropped`
- `agent/spawn` {agent{uid,name,player}, agentType, spawnType, playerColor, pos}
- `agent/health` {agent, delta, health, by}
- `agent/death` {agent, by, onClient}
- `inv/add` {agent, item, count} · `inv/drop` {agent, item}
- `door/open` {door, doorType, agent, remote}
- `level/generate` {seedNum, seedLetter, levelType, curLevel}
- `move/sample` {agent, pos} (every 25th fixed step per agent)
- `net/call` {hub, method, args[]} (every Cmd/Rpc body invocation)

Tooling: `node scripts/test/trace_summary.mjs <trace.jsonl>`.
