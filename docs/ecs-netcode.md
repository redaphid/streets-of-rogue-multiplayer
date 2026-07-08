# ECS netcode on Cloudflare Durable Objects

Goal: replace the game's Mirror networking with an ECS-based layer whose
authoritative room state lives in a Cloudflare Durable Object, so anyone can
play over the internet with no port forwarding, Steam, or Epic.

## Architecture

```
game instance A                    Cloudflare edge                   game instance B
┌──────────────────┐   wss   ┌─────────────────────────┐   wss   ┌──────────────────┐
│ EightPlayers mod │◄───────►│ GameRoom Durable Object │◄───────►│ EightPlayers mod │
│  EcsNet/         │         │  worker/src/room.ts     │         │  EcsNet/         │
│  - EcsWorld      │         │  - RoomWorld (ECS)      │         │  ...             │
│  - NetClient     │         │  - ownership rules      │         └──────────────────┘
│  - EcsNetManager │         │  - snapshot + fan-out   │
└──────────────────┘         └─────────────────────────┘
```

- **Entities** are server-assigned integer ids. **Components** are named JSON
  blobs (`pos`, `player`, ...). The Durable Object enforces ownership: only the
  connection that spawned an entity may `set`/`despawn` it.
- One Durable Object per room code (`idFromName`). WebSocket Hibernation API
  keeps idle rooms free; non-volatile components are persisted to DO storage so
  a room survives eviction. `pos` is volatile (memory + broadcast only).
- Late joiners get a full **snapshot** in the `welcome` message.
- Protocol: one JSON object per text frame, defined in `worker/src/protocol.ts`
  and mirrored in `EightPlayers/EcsNet/Protocol.cs`. Keep the two in sync.

## What works now (phase 0)

- `worker/` deploys with `npm run deploy` (dev: `npm run dev`).
- Mod config section `[EcsNet]` (or env `SOR_ECS_SERVER` / `SOR_ECS_ROOM` /
  `SOR_ECS_NAME`). Setting a room code enables the layer; every local player
  agent is published at `SendHz` (15/s default) and remote players appear as
  colored ghost markers with name labels. HUD line top-left shows connection
  state. Mirror is untouched — this runs alongside a normal local/LAN game.
- Tests: `scripts/test/ecs_room_sim.mjs` (protocol semantics, 14 assertions),
  `scripts/test/ecs_room_watch.mjs` (spectate a live room).

## Migration method (strangler-fig, per system)

Each vanilla system is replaced one at a time: (1) instrument its choke
points (docs/trace-choke-points.md) → (2) capture baseline traces + tests →
(3) implement the ECS version event-driven off the same choke points →
(4) `scripts/test/trace_diff.mjs` vanilla-vs-ECS → (5) flip the default.

Ported so far (per-system detail: [ecs-systems.md](ecs-systems.md)):
- **PvP damage**: hits on a remote player's avatar relay as `pvp-hit` events
  to the owner, who applies them authoritatively through vanilla
  ChangeHealth (each player owns their own hp); the resulting hp component
  converges everywhere. Local avatar damage is cosmetic.
- **NPC sync**: generation NPCs mirror by spawn-order index from the
  lowest-client-id authority (pos batched, hp, death); dynamic post-load
  spawns are suppressed on followers and mirrored from authority-published
  entities (pseudo-agents like ObjectAgent excluded). Regression gate:
  `scripts/test/e2e_scenario.sh` (18 assertions, two live instances).
- **Health**: `StatusEffects.ChangeHealth` hook (EcsHooks) marks the local
  player dirty; next publish tick sends an `hp` component; peers render it
  on ghost labels. First event-driven (non-polled) system.
- **World identity**: rooms carry a first-write-wins `world.seed` (claimed by
  the first client to reach a level, released when the room empties). Other
  clients adopt it via the game's user-set-seed path at their next game
  start, so everyone generates the same map. Player entities carry a
  `level {seed, num}` component; ghosts only render for same-world players.
  `SOR_SEED` env overrides everything (test determinism control).
- **Status effects** (2026-07-08): Add/RemoveStatusEffect masters publish a
  `status {e, name, on}` event for local players; peers mirror it on the
  avatar (popups suppressed). Natural expiry converges from the owner.
- **Door lock/unlock** (2026-07-08): `door-lock {x, y, locked}` from the
  Lock/Unlock chokes, flag-suppressed echo, position-addressed apply.
- **Object destruction** (2026-07-08): `obj-destroy {x, y, name}` from
  `ObjectReal.DestroyMe` (destroying-flag edge), idempotent apply.
- **Fire** (2026-07-08): `fire-spawn {x, y, oil}` / `fire-out {x, y}` from
  the SpawnFire master and Fire.DestroyMe; tolerance-deduped apply, emitter
  (`neverGoOut`) fires excluded, level-transition teardown never published.

## Future client note

A JavaScript/browser client is planned. The protocol is deliberately
language-neutral: one JSON object per WebSocket text frame, schema owned by
`worker/src/protocol.ts` (mirrored by `EightPlayers/EcsNet/Protocol.cs`).
When adding events keep payloads self-describing (no C#-specific encoding,
no reliance on game-instance state a browser wouldn't have), and document
them in [ecs-systems.md](ecs-systems.md) — that file doubles as the spec a
JS client will be written against.

## Phase plan

1. **Presence (done)** — players see each other as ghosts across the internet.
2. **Real remote agents (done)** — remote same-world players are real spawned
   `Agent`s (brain disabled), driven by `pos`, named via `player.name`,
   char type via `player.char`. Verified: teleporting one player moved the
   other instance's avatar within ~0.1 units.
3. **Deterministic world join (seed done, transitions pending)** — rooms carry
   a first-write-wins world seed all clients adopt; level-transition sync
   (travel together on elevator, `level/advance` trace hook in place) is next.
4. **Interactions** — component-ize the high-value verbs: damage, death, item
   pickup/drop, door/object state. Server validates writes per system
   (e.g. only the entity in range may open a door).
5. **NPCs & authority migration** — one client (first in room) simulates NPCs
   and publishes them; DO reassigns simulation ownership on disconnect.
6. **Mirror retirement (proven 2026-07-07)** — the full e2e gate (20
   assertions) passes with both instances in pure single-player mode
   (`E2E_MODE=solo`): NetworkServer never starts and every synced system
   works through the DO alone. Real usage: play single-player with
   `[EcsNet] Room` set. Formal entry-point patching is optional hardening.

Risks / notes

- The DO cannot run Unity logic, so simulation authority always lives on some
  client; the DO owns *state*, ordering, and membership.
- Newtonsoft ships with the game; `ClientWebSocket` comes from Mono's
  `System.dll` (net472) — no new runtime deps in the mod.
- JSON is fine at 15 Hz × 8 players; switch `pos` traffic to a binary frame if
  rooms ever feel chatty (protocol already allows binary frames — server
  ignores them today).
