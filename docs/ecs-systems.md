# ECS-synced systems — detailed reference

One section per ported system: what vanilla does, which choke points are
hooked, the wire event(s), how the remote apply works, how echo/duplication
is prevented, and where the e2e gate asserts it. Companion to
[ecs-netcode.md](ecs-netcode.md) (architecture) and
[trace-choke-points.md](trace-choke-points.md) (choke-point survey).

Conventions used by every system:

- **Publish side** = a Harmony hook on the vanilla choke point calls an
  `EcsNetManager.OnLocal*` method, which no-ops until `_welcomed` (the DO
  accepted us and sent the room snapshot).
- **Apply side** = a `case` in `EcsNetManager.OnMessage` finds the local
  twin of the thing that changed and mutates it through the SAME vanilla
  path a local cause would use (via `GameStateApi` where possible), so all
  downstream vanilla behavior (sprites, sounds, pathfinding, AI reactions)
  happens for free.
- **World objects are addressed by quantized position (+ name/type)**,
  never UID: PlayfieldObject UIDs come from an instance-local counter and
  drift between clients even on byte-identical worlds
  (`GameStateApi.FindDoorAt` / `FindObjectAt`, tolerance 0.5).
- **Echo suppression**: when the vanilla apply path is the same method the
  publish hook patches, a static `ApplyingRemote*` flag is held during the
  apply so the hook doesn't re-publish the event into the room.
- Every apply logs an `ECSNET ...` line; `scripts/test/e2e_scenario.sh`
  asserts on those lines from the OTHER instance's log.

## Position / presence (`pos`, `player` components)

- Publish: every local player agent publishes a `pos` component at `SendHz`
  (15/s default) from `EcsNetManager.Update`. `player {name, color, char}`
  and `level {seed, num}` are published on spawn/level change.
- Apply: `RemoteAvatars` spawns a real `Agent` (brain disabled, tinted by
  `player.color`) for every same-world remote player entity and lerps it to
  `pos`. Avatars only exist for entities whose `level.seed/num` match ours.
- `pos` is volatile on the DO (memory + fan-out, never persisted).
- e2e: [2/8] avatars both directions, [3/8] teleport follow.

## World identity (seed adoption)

- The first client to reach a level claims `world {seed, level}` on the
  room (first-write-wins, enforced by the DO; released when the room
  empties). Later clients adopt it via the game's user-set-seed path at
  their next game start (`Forcing map seed '<seed>' at level load`).
- Ordering caveat (seen in live testing): two instances launched
  simultaneously race — the loser generates its own world and no avatars
  render (different `level.seed`) until it restarts and adopts. The e2e
  launches sequentially for this reason.
- `SOR_SEED` env overrides everything (test determinism).
- e2e: [1/8] boot + shared world.

## Level transitions (travel together)

- The room's `world.level` advances when the claimer finishes a level;
  followers see the change and force-advance through the same elevator
  path (`ECSNET following room: level N -> M`).
- e2e: [8/8] travel together (B follows to level 2, avatars respawn).

## Health (`hp` component)

- Choke: `StatusEffects.ChangeHealth(...)` 6-arg master (all overloads
  funnel into it). Hook marks the local player dirty; the next publish
  tick sends `hp {cur, max}`. Event-driven, not polled.
- Apply: peers render hp on ghost labels / avatar health bars.
- e2e: [4/8] health sync.

## PvP damage (`pvp-hit` event)

- Hits landing on a remote player's AVATAR are relayed as
  `pvp-hit {e, dmg}` to the owning client, which applies them
  authoritatively through vanilla ChangeHealth — each player owns their
  own hp; the resulting `hp` component converges everywhere. Local avatar
  damage is cosmetic.
- e2e: [9/9] pvp hit lands on the real player, [10/10] lethal hit kills
  for real everywhere.

## Player death

- Choke: `StatusEffects.SetupDeath` (master, sets `agent.dead`). Publisher
  sends a dead-marked `hp` component for the local player; peers play the
  avatar's death through vanilla SetupDeath.
- e2e: [10/10] player death propagates.

## NPC sync (`NpcSync`)

- Authority: the LOWEST client id in the room simulates NPCs and publishes
  them (generation NPCs mirrored by spawn-order index — same seed means
  same spawn order; positions batched, hp, death). Followers bind twins by
  index and disable their brains.
- Dynamic post-load spawns are suppressed on followers and mirrored from
  authority-published entities (pseudo-agents like ObjectAgent excluded).
  Deliberate local spawns (avatars, command verbs) bypass suppression via
  `NpcSync.BypassSuppression`.
- Authority migration: when the authority disconnects the DO despawns the
  leaver's entities, followers unbind (re-enabling brains), and the new
  lowest client starts publishing.
- e2e: [7/8] NPC convergence + death.

## Ground items (`item-drop` / `item-pickup` events)

- Chokes: `InvDatabase.DropItem` master (publish drop with position, name,
  count) and `InvDatabase.PickUpItem` (publish pickup).
- Apply: drop spawns the same ground item at the same coordinates
  (`ground item '<name>' dropped`); pickup finds it by position + name
  (`FindGroundItemAt`, tolerance 0.75) and consumes it
  (`taken by peer`).
- e2e: [6/8] ground item round trip (A drops, B sees it, B picks it up,
  A's copy is consumed).

## Doors — open (`door-open` event)

- Choke: `Door.OpenDoor(Agent, bool)`. Hook publishes only when the opener
  is a LOCAL player (`myAgent.isPlayer > 0 && != 99`); remote applies call
  `door.OpenDoor(null)` so the hook's agent filter breaks the echo loop
  (no flag needed).
- Wire: `door-open {x, y}`; apply finds the door via `FindDoorAt`.
- e2e: [5/8] door sync.

## Doors — lock/unlock (`door-lock` event)  *(added 2026-07-08)*

- Chokes: `Door.Lock()` / `Door.Unlock()` (Door.cs:1537/1588). These take
  no agent — levers, keys, hacking and remote applies all funnel through
  them — so echo suppression uses the `EcsNetManager.ApplyingRemoteDoor`
  flag instead of an agent filter.
- The hooks publish only when the state actually changed
  (`__instance.locked` postfix check): in a Mirror game the non-authority
  branch routes through ObjectMult without touching `locked`, and that
  no-op must not be published.
- Wire: `door-lock {x, y, locked}`; apply calls `door.Lock()/Unlock()`
  under the flag, logging `... locked/unlocked by peer`.
- Publisher scope: ANY local lock/unlock (player or NPC initiated).
  Double-publish when both instances simulate the same cause is harmless —
  applying `locked=true` twice is idempotent.
- e2e: [5/8] lock + unlock round trip (grep note: the lock assertion
  matches `") locked by peer"` because "unlocked by peer" contains
  "locked by peer" as a substring).

## Status effects (`status` event)  *(added 2026-07-08)*

- Chokes: `StatusEffects.AddStatusEffect(...)` 6-arg master and
  `RemoveStatusEffect(...)` 5-arg master (all thinner overloads funnel in;
  hooks select them by `OrderByDescending(parameter count)` so a game
  update that adds an overload keeps working).
- Publish: only for LOCAL player agents (looked up in `_locals`). The Add
  hook is a postfix and the master can REFUSE the effect
  (`preventStatusEffects`, dead agent, Dizzy/DizzyB conflicts), so the
  publisher double-checks `hasStatusEffect(effect)` before sending "on".
  "off" is sent unconditionally (removing an absent effect is a no-op).
- Wire: `status {e, name, on}` where `e` is the player's entity id.
- Apply: `RemoteAvatars.GetAgentFor(entity)` resolves the avatar;
  `AddStatusEffect(name, showText: false)` /
  `RemoveStatusEffect(name, showText: false, playSound: false)` mirror the
  effect without text popups. Dead avatars are skipped.
- No echo flag needed: the hook publishes only agents in `_locals`, and an
  avatar is never a local player.
- Natural expiry converges: when the effect times out on the OWNER, vanilla
  calls RemoveStatusEffect there, which publishes "off" — peers never need
  their own timers.
- e2e: [11/11] Fast applied + removed on the avatar. Verified live: A
  poisoned at 80→50 hp showed `hp=50/80` on B with the damage bar over
  the avatar (see outputs/screenshots/shot-*-poisoned*.png).

## Object destruction (`obj-destroy` event)  *(added 2026-07-08)*

- Choke: `ObjectReal.DestroyMe(PlayfieldObject)` — the single entry to the
  `DestroyMe2` teardown coroutine (shrapnel, chunk updates, drops).
  Partial object damage (`ObjectReal.Damage` accumulation, flashes) stays
  local-cosmetic; destruction is the gameplay-relevant converging event.
- Publish: prefix/postfix pair watches the `destroying` flag flip
  false→true, so only a REAL destruction publishes (repeat DestroyMe calls
  no-op and don't publish). Any local cause publishes: player, NPC, fire,
  chain explosion — duplicates from twin simulations are fine because the
  apply side skips objects already `destroying`.
- Wire: `obj-destroy {x, y, name}`; apply via `FindObjectAt(pos, name)`
  under `ApplyingRemoteObject` (breaks the echo loop), calling
  `obj.DestroyMe(null)` so vanilla teardown runs.
- A missing local twin logs at Info, not Warning: chain reactions publish
  from both sides and the loser often finds the object already gone.
- e2e: [5b] A destroys the nearest object; B's twin (same name, same
  position) is destroyed by the event.

## Fire (`fire-spawn` / `fire-out` events)  *(added 2026-07-08)*

- Chokes: `SpawnerMain.SpawnFire(...)` 8-arg master (all 7 thinner
  overloads funnel in) and `Fire.DestroyMe()` — the single teardown that
  water, extinguishers and burn-out all reach.
- Publish (spawn): postfix fires only when the master returned non-null
  (it dedups by exact position / burning object and returns null on a
  duplicate). `neverGoOut` fires are NOT published: those belong to
  generation emitters (flame grates, fire spewers) that both instances
  already simulate identically — publishing them would be pure churn.
- Publish (out): `destroying` false→true edge (prefix/postfix state pair),
  suppressed during `levelTransitioning` (mass teardown is not gameplay)
  and under `ApplyingRemoteFire`.
- Wire: `fire-spawn {x, y, oil}`, `fire-out {x, y}`.
- Apply: vanilla's spawn dedup uses EXACT float equality, which will not
  match a twin fire that local spread simulation already created at a
  wire-rounded coordinate — so the apply first checks
  `GameStateApi.FindFireAt(pos)` (tolerance 0.35) and skips if anything
  burns there. Both instances run their own spread simulation; the
  cross-published events converge the two toward the union. Burn-out
  publishes from both sides; the apply skips missing/already-destroying
  fires.
- Fire DAMAGE to agents/objects is not separately synced: player hp is
  owner-authoritative (`hp` component), NPC hp comes from the NPC
  authority, and object destruction has its own event.
- Found by the red→green TDD cycle (first green run still failed):
  every ground fire spawns an invisible "ObjectAgent" helper via
  `Fire.SpawnObjectAgent` → `SpawnAgent`, and the follower-side dynamic
  NPC spawn suppression nulled it → NPE inside `SpawnObjectAgent`, a
  half-initialized fire, and an immediate bogus `fire-out` echo that
  killed the ORIGINAL fire on the igniting instance. Fix: the suppression
  prefix passes `agentType == "ObjectAgent"` through — mirroring the
  pseudo-agent exclusion the publish side (`RegisterNpcSpawn`) already
  had. Any follower fire (flame grate, molotov, spread) would have hit
  this, not just synced ones.
- e2e: [12/12] ignite propagates, extinguish propagates.

## Publish window (all world-object events)

`fire-spawn/out`, `obj-destroy` and `door-lock` only publish while
`gc.loadComplete && !gc.levelTransitioning` (`WorldStable`). Level
GENERATION mutates world objects (setup re-locks doors, clears props) and
teardown destroys everything — that state is seed-derived and every
instance produces it locally; publishing it spams peers mid-load with
positions they can't resolve yet (seen live as
`door-lock ... no door there locally` warnings during [8/8] travel).
`door-open` keeps its agent filter instead: it only ever publishes for a
local player, who can't act mid-load.

## Known non-determinism (accepted)

- NPC clothing/loadout CONTENTS diverge between instances (RNG consumption
  is frame-timing dependent) — twins match by index/position/hp but may
  dress differently. Cosmetic only; see the determinism findings in
  trace-choke-points.md.
- Movement samples drift with wall clock; `pos` convergence is what
  matters.

## Debug surface

Live command channel (`BepInEx/ep_cmd.txt` → `ep_out.txt`), superset of
the GameStateApi verbs. Sync-relevant commands:

```
state · ecs · npcs · agents · doors · objects · items · fires
hp <uid> <±delta> · kill <uid> · status <uid> <effect> [off] · statuses <uid>
give <uid> <item> [n] · drop <uid> <item> · pickup <uid> <x> <y> <item>
tp <uid> <x> <y> · opendoor <duid> [uid] · lockdoor <duid> [off]
destroyobj <ouid> · ignite <x> <y> · extinguish <x> <y>
spawnagent <type> <x> <y> · nextlevel
room <code> · leave · screenshot [file.png]
```

`screenshot` writes via `ScreenCapture.CaptureScreenshot`; with clone dirs
the relative path lands in the REAL game's `StreetsOfRogueLinux_Data/`
(clones symlink the Data dir).
