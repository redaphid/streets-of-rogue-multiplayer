# Claude e2e debug harness for Streets of Rogue

How to drive, observe, and manipulate live game instances programmatically —
the playbook for debugging any SoR component end-to-end with Claude (or any
automation). Everything here works headless (`-batchmode -nographics`) and
windowed (for screenshots/recording).

## 1. Launching instances

Clone dirs bypass Unity's single-instance lock (hard-linked exe, symlinked
data). Existing clones: `~/.var/app/com.valvesoftware.Steam/data/sor-clones/{ecs0,ecs1}`.

```sh
CL="$HOME/.var/app/com.valvesoftware.Steam/data/sor-clones"
flatpak run --command=sh \
  --env=SOR_TEST_MODE=solo --env=SOR_TEST_NAME=DBGA --env=SOR_TEST_PORT=7777 --env=SOR_TEST_ADDR=127.0.0.1 \
  --env=SOR_ECS_ROOM=MYROOM --env=SOR_ECS_SERVER=ws://127.0.0.1:8787 --env=SOR_ECS_NAME=DBGA \
  com.valvesoftware.Steam -c "C=\"$CL/ecs0\"; cd \"\$C\" && exec ./run_bepinex.sh -batchmode -nographics"
```

- `SOR_TEST_MODE=solo` auto-starts a single-player game (no menus);
  `host` self-hosts a Mirror LAN game instead. Omit for manual play.
- Drop `-batchmode -nographics` (add `-screen-fullscreen 0 -screen-width
  1280 -screen-height 720`) for a visible window — needed for
  screenshots/recording.
- ECS room play needs the worker: `cd worker && npm run dev`
  (ws://127.0.0.1:8787) or the deployed URL.
- **Launch instances SEQUENTIALLY** (wait for the first to claim the room
  seed) or the second generates its own world; the divergence heal will
  fix it, but it costs a reload.
- `SOR_SEED=<string>` forces a specific world (determinism).
- `SOR_TRACE=1` enables the trace layer (`traces/trace-*.jsonl`).

## 2. Command channel (read + write, per instance)

Write commands (one per line) to `<instance>/BepInEx/ep_cmd.txt`; polled
every 0.5 s, file is deleted after read; output appends to
`BepInEx/ep_out.txt` and to `LogOutput.log` as `EPCMD` lines.

Helper (used throughout `scripts/test/e2e_scenario.sh`):

```sh
cmd() { local d="$1"; shift; : > "$d/BepInEx/ep_out.txt"
  printf '%s\n' "$*" > "$d/BepInEx/ep_cmd.txt"
  for _ in $(seq 1 15); do
    [ -s "$d/BepInEx/ep_out.txt" ] && grep -q '>' "$d/BepInEx/ep_out.txt" \
      && { sleep 0.4; cat "$d/BepInEx/ep_out.txt"; return; }
    sleep 1
  done; echo TIMEOUT; }
```

### Observe

| Command | Shows |
|---|---|
| `state` | level type, agent/object counts, seed, local player line |
| `ecs` | connection state, room, entities, peers, npc authority |
| `agents` | every agent: uid, name, type, pos, hp, dead |
| `npcs` | NPC-sync registry: index, uid, entity, pos, dead |
| `doors` / `objects` / `items` / `fires` | nearest world objects with uid + pos |
| `statuses <uid>` | active status effects on an agent |
| `worldhash` | door-geometry fingerprint (divergence check) |
| `input` | current virtual-input state + player position |
| `dump` / `action <name>` | Rewired controller/binding introspection |

### Mutate state (through vanilla choke points — synced systems propagate)

| Command | Does |
|---|---|
| `hp <uid> <±delta>` | ChangeHealth (negative damages) |
| `kill <uid>` | SetupDeath |
| `status <uid> <effect> [off]` | Add/RemoveStatusEffect (e.g. Fast, Poisoned) |
| `give <uid> <item> [n]` / `drop <uid> <item>` | inventory add / drop to ground |
| `pickup <uid> <x> <y> <item>` | pick up a ground item |
| `tp <uid> <x> <y>` | teleport |
| `spawnagent <type> <x> <y>` | spawn an agent (bypasses follower suppression) |
| `opendoor <duid> [uid]` / `lockdoor <duid> [off]` | door verbs |
| `destroyobj <ouid>` | object destruction |
| `ignite <x> <y>` / `extinguish <x> <y>` | fire |
| `nextlevel` | advance level (peers follow via room) |
| `room <code>` / `leave` | join/leave an ECS room live |

### Drive the player (virtual input — real gameplay, not teleports)

Overwrites the game's held-input arrays post-read each frame
(`VirtualInput.cs`), so movement/combat run through the full vanilla
input→physics path in both controller modes.

| Command | Does |
|---|---|
| `move <dx> <dy> [seconds]` | hold a direction vector (default 1 s) |
| `walkto <x> <y> [timeout]` | steer toward a point (straight line — no pathfinding; chain waypoints around walls) |
| `hold <button> [seconds]` | hold attack / interact / special / useitem / cancel |
| `stop` | clear all virtual input |

Example — walk to an NPC and attack it:

```sh
cmd $A npcs                 # find a target pos
cmd $A "walkto 43.2 58.9 10"
cmd $A "hold attack 1.5"
cmd $A agents               # verify the hit landed
```

### Capture

- `screenshot [file.png]` — `ScreenCapture.CaptureScreenshot`; relative
  paths land in the REAL game dir's `StreetsOfRogueLinux_Data/` (clones
  symlink Data). Needs a graphical instance.
- `record <seconds> <fps> [dir]` — frame sequence from the game's own
  framebuffer (compositor-independent — x11grab shows BLACK under
  rootless XWayland/Wayland, and GNOME's Screencast dbus dies with the
  calling connection). Pre-create `<gamedir>/StreetsOfRogueLinux_Data/<dir>`,
  then encode + clean:
  `ffmpeg -framerate <fps> -i .../<dir>/f%05d.png -c:v libx264 -pix_fmt yuv420p out.mp4`
  (10 fps ≈ 3.3 MB/s of PNGs — delete the frames after encoding).

### Movement gates (why the character "won't move")

`Movement.PlayerMovement` is gated by a pile of flags; `input` dumps them
all. Ones that bite under automation:
- `gc.mainGUI.openedCharacterSelect` stuck true (no visible overlay!) —
  the TestDriver now clears it after one accept attempt.
- `cantPressButtons` while the level-start READ THIS brief is open — the
  TestDriver now dismisses it.
- In solo test mode the driver also sets `mustSelectCharacter=false`.

## 3. Logs and monitoring

- `BepInEx/LogOutput.log` per instance. Key prefixes: `EPCMD` (command
  channel), `ECSNET` (sync layer), `CTRLDBG` (controller state),
  `TESTDRIVER` (auto-play driver), `ZeroTwo` (pad mapping).
- Live watch: `tail -F .../LogOutput.log | grep -E 'ECSNET|EPCMD|Error|Exception'`
- Trace layer (`SOR_TRACE=1`): every choke point emits JSONL
  (`docs/trace-choke-points.md`); diff two instances with
  `node scripts/test/trace_diff.mjs a.jsonl b.jsonl --cat level,agent`.

## 4. The e2e regression suite

`scripts/test/e2e_scenario.sh [ROOM]` — boots two instances against local
wrangler, asserts every synced system (~30 assertions): seed adoption,
hash convergence, avatars, teleport-follow, hp, doors (open/lock/unlock),
object destruction, ground items, NPC sync, level travel, PvP, death,
status effects, fire. `E2E_MODE=solo` (pure single-player, Mirror off) or
`host` (Mirror LAN self-host). Prereqs: wrangler dev running, current dll
copied into BOTH clones' `BepInEx/plugins/`, no game instances running.

TDD loop for a new system: add the assertion → run (red) → implement →
deploy → run (green). The deployed-dll-vs-repo gap makes red runs honest.

## 5. Gotchas

- Player uid CHANGES per level in solo mode — re-resolve after any level
  change or heal reload (`state` → `player:` line).
- World-object UIDs drift between instances — always address by position
  (+ type). See `GameStateApi.FindDoorAt/FindObjectAt/FindGroundItemAt`.
- Same-seed generation can diverge (frame-timing RNG) — check `worldhash`
  on both instances first when positional syncs mysteriously fail; the
  mod heals it automatically (follower reloads, ≤2 attempts).
- `pkill -f <pattern>` from a shell whose own command line contains the
  pattern kills your shell — use `pkill -f 'StreetsOfRogue[L]inux'`.
- Flatpak: run the game inside the sandbox (host glibc too old for
  doorstop); paths under `~/.var/app/com.valvesoftware.Steam/`.
