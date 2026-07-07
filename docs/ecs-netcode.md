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

Ported so far:
- **Health**: `StatusEffects.ChangeHealth` hook (EcsHooks) marks the local
  player dirty; next publish tick sends an `hp` component; peers render it
  on ghost labels. First event-driven (non-polled) system.

## Phase plan

1. **Presence (done)** — players see each other as ghosts across the internet.
2. **Real remote agents** — replace ghost markers with spawned `Agent` objects
   (SetupAgent path, no brain/input), driven by `pos` + facing/anim components.
   Level identity component so only same-level players render.
3. **Deterministic world join** — sync level seed + elevator transitions so all
   clients generate the same map and travel together.
4. **Interactions** — component-ize the high-value verbs: damage, death, item
   pickup/drop, door/object state. Server validates writes per system
   (e.g. only the entity in range may open a door).
5. **NPCs & authority migration** — one client (first in room) simulates NPCs
   and publishes them; DO reassigns simulation ownership on disconnect.
6. **Mirror retirement** — patch out NetworkManagerUWP entry points; the game
   always runs "offline" locally and all multiplayer flows through the DO.

Risks / notes

- The DO cannot run Unity logic, so simulation authority always lives on some
  client; the DO owns *state*, ordering, and membership.
- Newtonsoft ships with the game; `ClientWebSocket` comes from Mono's
  `System.dll` (net472) — no new runtime deps in the mod.
- JSON is fine at 15 Hz × 8 players; switch `pos` traffic to a binary frame if
  rooms ever feel chatty (protocol already allows binary frames — server
  ignores them today).
