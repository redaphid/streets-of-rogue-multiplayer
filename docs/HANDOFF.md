# Project handoff / status

**Goal:** Up to 8 people play Streets of Rogue in ONE game across a mix of
computers. Phase 1 (Mirror LAN, 8-player cap, multi-window, controllers) is
DONE and summarized at the bottom. The active phase replaces Mirror with an
**ECS netcode layer on Cloudflare Durable Objects** (one DO per room, JSON
over WebSocket), designed so a JS/browser client can join later.
`docs/ecs-systems.md` is the protocol spec + per-system reference;
`docs/debug-harness.md` is the Claude-driven live-debug playbook.

Game: Flatpak Steam, **WINDOWS build via Proton 9.0** (Steam removed the
native Linux depot Jul 2026 — only `StreetsOfRogue.exe` +
`StreetsOfRogue_Data` remain), Unity 2022.3.60f1 Mono, AppID 512900, at
`~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue`.
BepInEx injects via `winhttp.dll` + `WINEDLLOVERRIDES=winhttp=n,b`. All csproj
`ManagedDir`s point at `StreetsOfRogue_Data/Managed` (with an automatic
fallback if the Linux depot ever returns). Test instances launch through
`scripts/test/proton_env.sh` (per-instance clone dir + wine prefix, seeded
with a copy of the real save — a blank prefix wedges menu automation).

## Branch map

- `main` — pre-ECS phase 1 + WizardMod.
- `ecs` — CANONICAL ECS BRANCH (this one): merge of `ecs-control-plane`
  (which fully contains `ecs-durable-objects` and `npc-dynamic-spawns`)
  plus main's Wizard/TestDriver fixes plus the Proton harness port.
  The old ECS branches are historical; work here.
- Plan: `docs/ecs-migration-plan.md` — the ship-of-theseus plank order.

## Current work: ECS control plane (branch `ecs-control-plane`)

Goal: control the player character and inspect world state **purely through
ECS** — no side-channel commands. Idiomatic pattern: control-as-data.

State at handoff (last commit `0174c00`):

1. **DONE — worker**: `input` is a **shared** component (any client may write
   it on any entity; a mixed write touching non-shared components is still
   owner-only) and **volatile** (broadcast, never persisted — an intent must
   not replay after DO hibernation). `worker/src/ecs.ts` (`SHARED`/`VOLATILE`
   sets), tests in `ecs.test.ts` — 11/11, `tsc` clean. Local `wrangler dev`
   hot-reloads this; the DEPLOYED worker does NOT have it yet.
2. **DONE — e2e sections written, NOT yet red-run**: `[14/14]` in
   `scripts/test/e2e_scenario.sh` — B writes `{"input":{"tx":X,"ty":Y}}` onto
   A's player entity via `ecsset` (exercises the shared-write rule), then
   watches A's `pos` move >2 units via B's `ecsget` only. `[14b]` — after
   `status <uid> Fast` on A, B's `ecsget` shows `"fx":...\"Fast\"`.
3. **TODO — C# InputSystem** (the red→green step): each frame, for each local
   player entity, read `Raw[entity]["input"]` from `EcsWorld` and feed
   `VirtualInput`: `{tx,ty}` → walkto, `{mx,my}` → held axes,
   `{hold:["attack",...]}` → held buttons, `null`/absent → clear. Apply only
   on the OWNING client. Wire a `Tick()` from `Plugin.Update`.
4. **TODO — `fx` component**: in `EcsNetManager.OnLocalStatusChanged`, also
   `Set` the player entity's `fx` component to the full current status list
   (e.g. `{"fx":{"list":["Fast"]}}`), so statuses are inspectable, not just
   event-observable.
5. Then: red run (expect [14]/[14b] FAIL only) → implement → build+deploy dll
   → green run solo, then host → docs (`ecs-systems.md` control-plane
   section) → commit+push.

## Operational rule (revised 2026-07-10)

Proton test instances COEXIST with a running Steam client (verified live —
the 2026-07-08 native-build crash-loop via shared CEF htmlcache no longer
applies; the user's own `scripts/start.sh` relies on coexistence).
`e2e_scenario.sh` warns instead of aborting. Still true: never overwrite a
plugin dll while any instance is running (Mono lazy-loads metadata and
hard-aborts), and kill strays with `pkill -9 -f 'StreetsOfRogue[.]exe'`.

## Running the gates

```sh
cd worker && npm run dev &          # wrangler dev on ws://127.0.0.1:8787
E2E_MODE=solo scripts/test/e2e_scenario.sh   # and E2E_MODE=host (default)
```

Build + deploy the plugin (game dir AND both clones `ecs0`/`ecs1` under
`~/.var/app/com.valvesoftware.Steam/data/sor-clones/`):

```sh
cd EightPlayers && ~/.dotnet/dotnet build -c Release
# then cp bin/Release/net472/EightPlayers.dll to <game>/BepInEx/plugins/ and both clones
```

TDD discipline: run the failing e2e assertion (red) BEFORE implementing.
Worker tests: `cd worker && npm test`.

## Release v0.1.0 (Windows, for the user's nephew)

https://github.com/redaphid/streets-of-rogue-multiplayer/releases/tag/v0.1.0 —
`EightPlayers-Windows-v0.1.0.zip`: BepInEx 5.4.23.3 win x64 + plugin from
`f9d6f0f` + kid-friendly HOW-TO-INSTALL.txt. F9 at the main menu opens the
co-op room join window. Worker deployed to
`wss://sor-ecs-net.loqwai.workers.dev` (version e0c93e16) and the bundled
config points at it. **Blocked on the user:** (1) repo is private — nephew
can't download; (2) Cloudflare Access intercepts `*.loqwai.workers.dev` with
a login redirect — exempt the worker or use an unprotected custom domain.
Untested: `wss://` TLS from the game's Mono runtime (all e2e is local `ws://`).

## Artifacts

- `outputs/recordings/gameplay-2min.mp4` (125 s) and `-2x.mp4` (62 s) —
  ECS-connected session, character driven via VirtualInput, frames verified.
- `outputs/screenshots/` — synced-state evidence shots (poison, banana, etc.).
- Recording method: in-game `record <seconds> <fps> [dir]` command → PNG
  frames → ffmpeg. x11grab is BLACK under rootless XWayland; don't use it.

## Key architecture facts (details in docs/ecs-systems.md)

- Components: `player`, `pos` (volatile), `hp`, `level {seed,num,hash}`,
  `npc {i,type}`, `dead`, `weapon`, `wlayout {lv,objs[]}` (one entity per
  level, authority-published, follower-reconciled; array index = `wi`
  address), NEW `input` (shared+volatile intent).
- Events: door-open/lock, obj-destroy, chest-take (all `wi`-addressed,
  position fallback), shop-take (`ni` NPC index), fire-spawn/out, gas-spawn,
  item-drop/pickup (positional), pvp-hit, status (player-entity addressed).
- World divergence: same seed can generate different worlds (frame-timing
  RNG). `level.hash` detects; heal = lower client id wins, follower reloads
  (≤2/level). `wlayout` reconciliation makes object addressing drift-proof.
- Level-load wedges: 3 root causes fixed + `LoadWatchdog.cs` (45 s empty-level
  detector → bounded reload, `reloadlevel` command to force).
- Player uid AND player entity change per level (solo) — always re-resolve.
- `gc.loadCompleteReally`, never `gc.loadComplete`, for "world is up".

## Phase 1 summary (done, on `main`)

8-player cap patches (`NetworkSlots_Patch`, `ServerFull_Patch` transpiler),
LAN menu re-enabled, multi-window via clone dirs (hard-linked exe, symlinked
data — Unity's `unity.lock` lives next to the exe), one gamepad per window
(`SOR_PAD=N` + `JoystickBinding.cs`), 8BitDo Zero 2 auto-map, Galaxy/GOG
fallback suppression, Steam launch options
`SOR_PAD=1 ./run_bepinex.sh # %command%`, Steam Input must be disabled
per-game. Old dist zips under `dist/` are stale; v0.1.0 release supersedes.

## Task list truth

Tasks #1-4, 6-15 completed. #5 (split-screen inside online games) pending and
superseded in practice by multi-window + ECS rooms. Active loop: the user
runs a standing /loop "until all systems are ported, and tested e2e" — never
self-terminate it; the control-plane work above is its current iteration.
