# Ship-of-Theseus ECS migration plan

The goal, refined 2026-07-10: **extract Streets of Rogue's RULES into an
ergonomic, deterministic ECS simulation** — one plank at a time, while the
ship sails — so that (a) Claude can interact with the live rules through an
MCP server, (b) rules e2e becomes deterministic-by-construction golden
replays in milliseconds instead of 20-minute Unity gates, and (c) rules
interact through shared components (fire spreads to anything `flammable`),
enabling the emergent behavior at the heart of the genre. The Unity game is
progressively demoted to renderer/actuator. Each swap is verified e2e before
AND after, TDD style, with gameplay video as evidence. No big-bang cutover,
ever; every commit leaves a playable game.

## Two repos

- **`rogue-brain`** (github.com/redaphid/rogue-brain, private) — the game
  brain: deterministic sim kernel (fixed tick, seeded PRNG in world state,
  queries, snapshot/restore, `hashWorld` golden replays), extracted rule
  systems, MCP server, and eventually the Durable Object host + JS client.
  NEVER contains decompiled game code — rules are re-authored from observed
  behavior. See its docs/HANDOFF.md.
- **This repo** — the Unity-side actuator/view: EightPlayers mod, TestDriver,
  Proton e2e harness, trace/parity tooling, and the decompiled reference
  (local-only, gitignored). It is the **parity oracle**: JSONL traces
  captured here are the fixtures rogue-brain systems are verified against.
  The 41-assertion gate here is the cross-repo integration test.

The current worker protocol (`worker/src/protocol.ts` ↔ `Protocol.cs`,
hand-synced) is **transitional scaffolding** for the Unity-computes era —
ownership rules, seed claiming, position-addressed events and divergence
healing all exist only because two Unity sims must converge. The durable
seam is versioned **intents in / state out**; the legacy messages shrink
plank by plank. The worker stays in this repo until the first sim-hosted
plank lands in rogue-brain, then migrates there.

## Known hack piles and their clean replacements

Two places where the current code fights the game instead of owning state —
each has an approved clean pattern that a plank replaces it with:

1. **Generation determinism forensics** (`ForceSeed_Patch`: qualified seed,
   randomSeedNum re-derivation, usedChunks clear, sessionData seed zeroing).
   Works (41/42), but it's whack-a-mole against hidden generation inputs.
   Clean pattern (already user-approved in ecs-systems.md): **wlayout
   adoption** — the authority's layout is authoritative room state; followers
   spawn/remove local objects to match instead of praying regeneration
   converges. Seed normalization then becomes a cheap first-order alignment.
2. **Input injection whack-a-mole** (VirtualInput array pokes + pressed-edge
   pulses + keyCheck/keyCheckHeld postfixes — the game reads input from at
   least three different places). Clean pattern: **one Rewired
   CustomController per local player** driven by the `input` intent
   component — flows through every GetButton/GetAxis path like a real
   device, deletes all the patches. (keyCheck/keyCheckHeld postfixes were
   tried 2026-07-10 and did NOT make guns fire — there are more direct-read
   sites; site-by-site patching is rejected.) e2e [15/15] stays red as this
   plank's standing TDD marker.

Companions: [ecs-systems.md](ecs-systems.md) (per-system reference of what is
already synced), [ecs-netcode.md](ecs-netcode.md) (architecture),
[debug-harness.md](debug-harness.md) (live-debug playbook),
[HANDOFF.md](HANDOFF.md) (session resume).

## Where we are

Done and gated (branch `ecs`, merged from `ecs-control-plane`):

- **Netcode replaced**: every gameplay sync system rides ECS rooms, not
  Mirror — presence/avatars, seed adoption + divergence healing, level
  transitions, hp, pvp damage/death, NPCs (authority model), ground items,
  doors, object destruction, fire, gas, chests, shops, statuses, weapons,
  wlayout. Solo mode (Mirror never starts) passes the same gate as host
  mode: Mirror-independence is proven, but Mirror code is still present.
- **Control plane begun** (the first "logic OUT of the game" plank):
  `input` intent component drives the owning client's character
  (e2e [14/15]); first worker-side system — `pvp-hit` damage — computes
  hp/dead authoritatively in `worker/src/systems.ts` (vitest-covered).
- **Harness**: 41-assertion e2e gate (`scripts/test/e2e_scenario.sh`,
  solo + host), worker vitest, C# xunit, JSONL behavior traces +
  `trace_diff.mjs`, live command channel, in-game video recorder
  (`E2E_VIDEO=1` records every gate run to `outputs/recordings/`).
  All of it now runs the **Windows build under Proton** (Steam removed the
  native Linux depot Jul 2026; see `scripts/test/proton_env.sh`).

## Target architecture (the fully-replaced ship)

- Worker systems (TypeScript, in the DO) own all **rule outcomes**: damage,
  death, inventory transactions, door/object state, status lifecycles, level
  progression. Clients send **intents/observations**; the room applies
  systems and broadcasts **authoritative component writes**.
- The Unity client degrades to: input capture → intent publish; component
  apply → vanilla presentation paths (via `GameStateApi`); local simulation
  only where latency demands prediction (movement) or where Unity is the
  only simulator we have (NPC brains, physics), published as observations.
- Mirror is deleted from the mod's patch surface; a JS client can join a
  room and be a real player (the wlayout + components already describe the
  world without Unity).

## The plank-swap loop (repeat per system)

The strangler-fig discipline, refined by what phase 1 taught us:

1. **RED** — write the worker unit tests (vitest) for the system's rules,
   and add/extend the e2e section asserting the OUTCOME arrives as a
   server component write on both instances. Run the e2e section: it must
   fail for the right reason before any implementation. (TDD rule from
   [tdd-discipline]: red first, always.)
2. **Baseline** — capture a JSONL trace of the vanilla behavior
   (`SOR_TRACE=1`, choke points per `trace-choke-points.md`) and record a
   short gameplay clip of the vanilla behavior (`E2E_VIDEO=1`).
3. **Worker system** — implement in `worker/src/systems.ts` until vitest is
   green. No game needed for this step; this is where the logic now lives.
4. **C# swap** — the publish hook stops applying the outcome locally and
   publishes the intent/event instead; the apply side consumes the
   authoritative write through the SAME vanilla path a local cause would
   use. The old client-side path stays behind a kill-switch env
   (`SOR_ECS_SYS_<NAME>=0` reverts) until the plank has soaked.
5. **Parity** — full gate green in BOTH `E2E_MODE=solo` and `host`;
   trace-diff vanilla vs ECS run; watch the recorded clips — video is the
   review artifact for "does it FEEL identical", which traces can't answer.
6. **Flip + evidence** — make the worker path the default, commit with the
   clip path in the message, push. One system per commit series; branch
   first when unsure ([loop-discipline]).

A plank is DONE when: vitest green, both gate modes green, trace parity
clean (or the diff is understood and documented), a clip exists, and the
kill-switch is documented in ecs-systems.md.

## Plank order

Ordered so each step reuses the machinery of the previous one, hardest
schema questions last. Steps 1–3 move existing event flows; 4–6 introduce
server-owned state for things that are today position-addressed events;
7–9 move ownership of whole domains.

| # | Plank | What moves into the worker | Builds on |
|---|-------|---------------------------|-----------|
| 1 | **Damage/death** | `pvp-hit` → server-computed `hp`/`dead` (started: systems.ts + wip C# apply) | control plane |
| 2 | **All player hp changes** | every local `ChangeHealth` becomes an intent (`hp-delta {e, amount, cause}`); server clamps, kills, broadcasts | 1 |
| 3 | **Status effects** | `status` event → server holds `fx` list + expiry timestamps (DO alarm ticks expiries); clients render | 2 |
| 4 | **Doors** | door state = server component keyed by `wi` (`doors {wi: {open,locked}}`); open/lock become intents the worker validates | wlayout |
| 5 | **Object destruction** | `alive` flags on wlayout indices; `obj-destroy` intent → server marks + broadcasts | 4 |
| 6 | **Inventory transactions** | ground drop/pickup, chest-take, shop-take → server-owned item entities / container components; dupes become impossible by construction | 5 |
| 7 | **Fire & gas lifecycle** | fire entities owned by the room (spawn/spread-union/out as server writes); clients simulate visuals only | 5 |
| 8 | **NPC combat state** | NPC hp/death computed by worker from hit intents; authority client keeps publishing pos/brains as observations | 2, 6 |
| 9 | **Level progression** | `world.level` advance validated server-side (all players at elevator / vote rule) | 3–8 soaked |

Then the two retirement milestones:

- **Mirror removal** — delete the Mirror-era patches and code paths from
  the mod (the solo-mode gate already proves nothing needs it); the LAN
  menu becomes the rooms UI. Gate: full e2e green with Mirror assemblies
  never loaded.
- **JS client proof** — a headless Node client joins a room, renders
  wlayout + entities, walks via `input` intents, takes damage from a
  Unity player. That is the moment the "ship" is demonstrably the new one:
  a player with no Unity at all.

Known debt to burn down along the way (tracked, not blocking plank 1):
the re-mirror anomaly (mirrors respawn more often than registrations),
deployed-worker access (Cloudflare Access still intercepts
`*.loqwai.workers.dev`), and `wss://` TLS from the game's Mono runtime
(all e2e is local `ws://`).

## Latency rule of thumb

The DO round trip is ~10–100 ms. Planks whose outcomes are **discrete**
(damage, transactions, doors, statuses, progression) tolerate it invisibly.
Continuous things (movement, aiming, physics) stay client-simulated with
`pos` observations — if a future plank wants server movement validation, it
must ship client prediction in the same step. Don't move a continuous
system to the worker just for purity.

## Running the gates

```sh
cd worker && npm run dev &                    # DO on ws://127.0.0.1:8787
E2E_MODE=solo scripts/test/e2e_scenario.sh    # and E2E_MODE=host (default)
E2E_VIDEO=1  E2E_MODE=solo scripts/test/e2e_scenario.sh   # + recordings
cd worker && npm test                         # worker systems, no game needed
```

Build + deploy the plugin (clones refresh themselves from the main install
at every gate run):

```sh
DOTNET_ROLL_FORWARD=LatestMajor ~/.dotnet/dotnet build EightPlayers -c Release
cp EightPlayers/bin/Release/net472/EightPlayers.dll \
   "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue/BepInEx/plugins/"
# same for TestDriver/bin/.../SorTestDriver.dll — and NEVER while a game instance is running
```
