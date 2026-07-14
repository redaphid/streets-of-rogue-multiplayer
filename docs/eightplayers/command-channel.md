# EightPlayers command channel — full verb & events reference

The command channel is the programmatic control surface of a **running** Streets of
Rogue instance carrying the EightPlayers mod. This is the complete reference,
generated from `EightPlayers/CommandChannel.cs` (the `Execute()` switch is the
single dispatch point) and `EightPlayers/HttpChannel.cs`. The verb tables in
`docs/debug-harness.md` are a *subset* of this list — when they disagree, this
doc and the source win.

**Primary consumer:** `~/Projects/rogue-gm` (the live AI game-master) drives its
whole observe → decide → act loop through this channel; many verbs exist because
of specific rogue-gm issues (cited inline in the source, e.g. `label` = #17,
`quest` = #22/#7, `setgoal` = #3).

## Transports

Two transports execute through the **exact same code path** (`CommandChannel.RunLine`
→ `Execute`), so replies are byte-identical.

### 1. File polling (fallback, always on)
Write verb lines (one per line, `#` comments ignored) to `<BepInEx>/ep_cmd.txt`.
The mod polls every **0.5 s** (`CommandChannel.Tick`), deletes the file after
reading, and **appends** replies to `<BepInEx>/ep_out.txt` (each command echoed
as `> cmd` before its output). Replies are also mirrored to `LogOutput.log`
with the `EPCMD` prefix. Latency ≈ 0.5–1 s per round trip.

### 2. Streaming HTTP (fast path)
`HttpChannel` binds `http://127.0.0.1:<port>/`:

- Port = `EP_HTTP_PORT` env var or **7801**; if busy (multiple game instances on
  one machine) the next 20 ports are probed. The **actual** bound port is written
  to `<BepInEx>/ep_port.txt` — always discover via that file.
- `POST /cmd` — body = raw verb line(s), plain text. Returns **200** with the
  exact reply text the file channel would have appended (including the `> cmd`
  echo). Verb errors are formatted in-band (`error: ...`) and still return 200;
  **5xx is reserved for channel-level failures** (503 = main thread didn't drain
  the queue within 30 s — game loading or wedged; retry or fall back to the file
  channel).
- `GET /events` — long-lived chunked response streaming newline-delimited JSON
  (NDJSON — deliberately **not** SSE). Event kinds:
  - `{"event":"hello","port":N}` — sent immediately on subscribe.
  - `{"event":"level_loaded","level":N,"seed":N}` — load-complete transition or
    new (seed, level) pair.
  - `{"event":"agent_died","uid","name","type","isPlayer","killerUid"}` —
    alive→dead diff, checked every 0.25 s while subscribed. Baselines are primed
    on first subscribe, so deaths that happened while nobody listened are NOT
    replayed. `killerUid` is the finishing agent (`killedByAgent` →
    `killedByAgentIndirect` → `lastHitByAgent`), or null if unknown.
  - `{"event":"agent_killed","uid","killerUid","killerName","killerIsPlayer"}` —
    the same death, but only when a killer is known: kill ATTRIBUTION the GM can
    key on ("who killed whom" — was it the player, a summon, a ghost?). ops-log §1.
  - `{"event":"status_applied","target","effect","isPlayer"}` /
    `{"event":"status_expired","target","effect","isPlayer"}` — a status-effect
    name appeared on / left a PLAYER or mod-CONTROLLED body (diffed every
    0.25 s). Makes transient effects (GIANT, Fast, Enraged) visible in the moment
    AND remembered after they expire, instead of leaving zero trace. ops-log §7b.
  - `{"event":"player_hp","uid","player","hp","hpMax","delta"}` — player health
    swings ≥ 3 hp.
  - `{"event":"menu_choice",...}` — pushed by DialogueMenu when a player presses
    a `setmenu` option.
  - `{"event":"quest_complete",...}` — pushed by StoryQuests on completion.
- `GET /state` — runs the `state` verb; `curl http://127.0.0.1:$(cat ep_port.txt)/state`.

Threading: HTTP accepts on background threads, but verb **execution always hops
to the Unity main thread** (queue drained by `HttpChannel.Tick`); the request
blocks until its verb completes. Multiple in-flight requests are fine.

## Argument conventions

- `<uid>` — an agent UID, or the aliases `player` / `player:N` (N = 1-based local
  player number). Resolved by `GameStateApi.ResolveUid`.
- Coordinates are world-unit floats; tile verbs (`clearmap`) floor them to cells,
  so positions handed back by `nearby`/`agents` can be passed straight in.
- "rest of line" args (`say`, `label`, `behavior`, `set`, `call`) preserve spaces.
- Every mutation goes through vanilla choke points, so it shows up in the
  `SOR_TRACE` behavior trace (how the e2e tests assert) and takes real game paths.

## Verbs

### Controllers (Rewired)
| Verb | Effect |
|---|---|
| `dump` | Controllers, players, maps, bindings, current axis values |
| `action <name>` | Current value + all bindings of one action |
| `bind <action> <+\|-> <element name>` | Button-style binding (player 0 joystick "Gamepad" maps) |
| `bindaxis <action> <element name>` | Full-axis binding |
| `unbind <action>` | Remove all bindings of the action (p0 joystick maps) |
| `remap` | Re-run the 8BitDo Zero 2 auto-layout from scratch |
| `nintendo <on\|off>` | Flip printed-label mode and remap |
| `enable <category> <on\|off>` | Enable/disable a joystick map category for p0 |

### World read
| Verb | Reply |
|---|---|
| `state` | Level/seed/agent summary (`GameStateApi.Summary()`) |
| `agents [people]` | One line per live agent (uid, type, pos, hp). `people` drops ObjectAgent backers and dead agents (rogue-gm#15) |
| `npcs` | EcsNet NPC-registry description |
| `objects` | 15 nearest ObjectReals to player (uid, name, pos, destroying); doors excluded |
| `containers` | 10 nearest item-holding objects (uid, name, pos, item count) |
| `chestitems <x> <y>` | Items inside the container at that position |
| `doors` | 10 nearest doors (uid, type, open, pos) |
| `items` | 10 nearest ground items (name, pos) |
| `fires` | Up to 15 fires (pos, destroying) |
| `nearby <x> <y> <radius> [people]` | JSON: agents + objects within radius |
| `inventory <uid>` | One-shot JSON inventory listing |
| `statuses <uid>` | Comma-joined active status effects |
| `worldhash` | FNV-1a door-geometry fingerprint + seed + level (divergence detection) |
| `input` | Current virtual-input state (`VirtualInput.Describe()`) |
| `labels` / `quests` / `behaviors` | List active labels / story quests (JSON) / Lua behaviors |

### Agent mutate
| Verb | Effect |
|---|---|
| `spawnagent <type> <x> <y>` | Spawn an NPC (e.g. `spawnagent Thief 10 12`); replies with its uid line |
| `hp <uid> <delta>` | Change health (negative damages); replies new hp |
| `kill <uid>` | Kill an agent |
| `status <uid> <effect> [off]` | Add (default) or remove a status effect |
| `say <uid> <text...>` | Pop the agent's in-game speech bubble (rest of line) |
| `give <uid> <item> [count]` | Add inventory item |
| `drop <uid> <item>` | Drop inventory item |
| `equip <uid> <weapon>` | Equip a weapon from inventory |
| `tp <uid> <x> <y>` | Teleport an agent |
| `recruit <uid>` | Recruit the NPC into the player's party (also flags it AI-controlled → stock chatter muted, §8a) |
| `brainactive <uid> <on\|off>` | Wake/sleep an agent's brain (active-brain-list surgery) |
| `setgoal <uid> <goal> [<targetUid\|player> \| <x,y> \| <x> <y>]` | Inject a REAL brain goal: Follow/Guard/Battle/Flee/Investigate/Wander/WanderFar |
| `pin <uid> <x> <y>` / `unpin <uid\|all>` | Per-frame position lock overriding all movement (staged beats); unpin releases |
| `aimarker <uid> <on\|off>` | Cosmetic cyan glow marking AI-driven agents; ALSO flags the body AI-controlled → mutes its native stock chatter (§8a) |
| `aicontrol <uid> <on\|off>` | Flag a body mod-controlled WITHOUT the glow: mutes its native stock chatter so it speaks only via `say` (§8a). Auto-cleared on the body's death or a level change (§8b) |
| `walknpc <uid> <x> <y>` | EXPERIMENTAL: NPC walks via own pathfinding (brain may re-route) |
| `pickup <uid> <x> <y> [itemName]` | Agent picks up a ground item at position |

### World mutate
| Verb | Effect |
|---|---|
| `spawnobject <name> <x> <y>` | Spawn an ObjectReal; replies the **settled** uid (pooled objects re-register — the raw instance uid can pre-date setup; see UID-drift note in source) |
| `destroyobject <uid>` (alias `destroyobj`) | Cleanly remove an ObjectReal (RemoveMe: no wreckage/SFX/fire) |
| `destroywall <x> <y>` / `buildwall <x> <y>` | Remove / place a wall cell |
| `buildmap <base64>` | Materialize an authored `.map` ASCII grid onto the live floor (rogue-gm#20). Reply JSON: `{"anchors":{NAME:{x,y}},"built":{...},"bounds":{...}}` |
| `clearmap <x> <y> <w> <h>` | Raze a rectangle to bare floor (top-left cell; w east, h south; floats floored to cells) |
| `opendoor <uid> [agentUid]` | Open a door by UID (optionally attributed) |
| `lockdoor <uid> [off]` | Lock (default) / unlock a door; replies `locked=` |
| `explode <x> <y> [type]` | Explosion (default `Normal`) |
| `ignite <x> <y>` / `extinguish <x> <y>` | Start / stop fire at position |
| `spawngas <objectUid> [type]` | Vent gas from an object (default `Flammable`) |
| `gascloud <x> <y> [type]` | Free-standing gas cloud (default `Poison`) |
| `chestgive <x> <y> <item>` / `chesttake <x> <y> <item>` | Put/take item in the container at position |
| `shoptake <uid> <item>` | Take an item from a shopkeeper agent |
| `nextlevel` | Trigger the elevator/floor transition |
| `reloadlevel` | Force a bounded level reload (LoadWatchdog path) |

### Dialogue / quests / labels
| Verb | Effect |
|---|---|
| `setmenu <uid> <b64json>` | Custom NPC talk menu or prefetched conversation TREE. JSON = `["opt",...]` or `[{"text","reply"?,"next"?:[...]},...]` — recursive, ≤6 options × 40 chars per level, replies ≤90 chars, depth ≤5, ≤40 nodes; `[]` flags the uid with "...". Presses stream as `menu_choice` events |
| `clearmenu <uid\|all>` | Restore the vanilla talk menu |
| `label <uid\|player[:n]> <TEXT...>` | Quest-marker-style world-space text over an agent OR object (uid may be an ObjectReal uid). Rest of line, case preserved (vanilla style is ALL CAPS), ≤48 chars |
| `clearlabel <uid\|all>` / `labels` | Remove / list labels |
| `quest add <id> <uid\|x,y> <reach\|kill\|interact\|protect> <TEXT...>` | Register a story quest: ALL-CAPS marker + native mission-sheet row; completion pushes `quest_complete` on `/events` |
| `quest done <id>` / `quest clear <id\|all>` / `quests` | Force-complete / drop / list (JSON) |

### Code mode (Lua, BehaviorEngine)
| Verb | Effect |
|---|---|
| `behavior <uid\|player[:n]> <lua...>` | Install/replace a per-agent Lua script (rest of line — single-line scripts only); flags the body AI-controlled → stock chatter muted (§8a) |
| `behaviorb64 <uid> <base64> [hz]` | Preferred form: newlines survive; default 10 Hz; also flags AI-controlled |
| `behaviors` | List `{uid,hz,errors,enabled,bytes}` |
| `clearbehavior <uid\|all>` | Remove behavior(s); clears the AI-controlled flag (restores stock chatter) |

Scripts define `tick(api)`; see `EightPlayers/BehaviorEngine.cs` for the API
(`self/nearby/player/dist/moveToward/teleport/say/hp/attackNearest/status/setGoal/takeControl/time/ignite/label/mem`)
and sandbox limits (MoonSharp HardSandbox, instruction cap, auto-disable after
5 consecutive errors or 10 slow ticks). Requires `MoonSharp.Interpreter.dll`
alongside the plugin DLL.

### Reflection (Reflect.cs — "god view")
Targets: `gc` | `agent:<uid>` | `player[:<n>]` | `handle:<id>` | `static:<Type>`.

| Verb | Effect |
|---|---|
| `inspect <target>` | Overview of the target object |
| `get <target> <path>` | Read a member path |
| `getmany <target> <p1>\|<p2>\|...` | Batch read, one round trip (pipe-separated, rest of line) |
| `keys <target> <path>` | String keys of a dictionary member |
| `set <target> <path> <value...>` | Write (value = rest of line) |
| `call <target> <method> [jsonArgs]` | Invoke a method (json = rest of line) |
| `find <name> [max=25]` | Find objects/members by name |
| `members <TypeName> [fields\|props\|methods\|all] [nameFilter]` | Type member listing |
| `types <filter> [max=50]` | Search loaded types |

Output is budget-capped; object references come back as `handle:<id>`
(WeakReference registry) usable as later targets.

### ECS / room (Cloudflare netcode)
| Verb | Effect |
|---|---|
| `ecs` | `EcsNetManager.DebugDump()` connection/room state |
| `room <code>` / `leave` | Join / leave a room |
| `entities` | List ECS entities (harness view, includes raw unknown components) |
| `ecsget <entity>` | One entity as JSON |
| `ecsset <entity> <json>` | Send a component set (json must contain no spaces on the file channel) |
| `ecsevent <name> [json]` | Emit a wire event |

### Player input (VirtualInput)
| Verb | Effect |
|---|---|
| `move <x> <y> [secs=1]` | Hold a movement direction vector |
| `walkto <x> <y> [timeout=15]` | Walk the player to a position |
| `hold <button> [secs=0.5]` | Hold a virtual button (interact etc.) |
| `stop` | Clear all virtual input |

**Known limitation** (`EightPlayers/VirtualInput.cs:133-142`): virtual buttons
drive the held/pressed arrays, which covers movement and some interactions, but
**weapon firing reads input through other paths** (`PlayerControl.keyCheck` /
`keyCheckHeld` / `useItemCheck` / `pressButtonDown` query Rewired devices
directly) — `hold attack` cannot fire a gun. The clean fix is a Rewired
CustomController per local player driven by the ECS `input` intent (see
`docs/ecs-migration-plan.md`); e2e [15/15] is its standing red marker.

### Capture
| Verb | Effect |
|---|---|
| `screenshot [path]` | `ScreenCapture.CaptureScreenshot` — written by the game next frame; default `screenshot-HHmmss.png`, resolves under the game `<Data>` dir |
| `record <seconds> <fps> [dirname=rec]` | Frame sequence via the game framebuffer (compositor-independent); dir must exist; encode externally with ffmpeg |

## How to add a new verb

The pattern, end to end (this recipe was previously undocumented):

1. **Dispatch:** add a `case "myverb":` to the switch in
   `CommandChannel.Execute()` (`EightPlayers/CommandChannel.cs`). Parse args from
   `parts[]` (pre-split into 4) or re-split `cmd` for rest-of-line args. Reply
   with `Out(...)` — it routes to ep_out.txt or the HTTP capture automatically.
   Update the header comment (lines 10–77) — it is the in-repo verb reference.
2. **Mutation:** implement the actual game mutation as a method on
   `GameStateApi` (`EightPlayers/GameStateApi.cs`), going through a **vanilla
   choke point** (SpawnerMain, StatusEffects.ChangeHealth, Door.OpenDoor, ...) so
   the action shows up in the SOR_TRACE trace and takes real game code paths —
   never mutate fields directly when a game method exists.
3. **Testability (optional):** put Unity-free logic in a `*Core.cs` class
   (pattern: `DialogueMenuCore`, `StoryQuestCore`, `MapBuilderCore`, `LabelCore`)
   so `EightPlayers.Tests` can unit-test it without the game.
4. **Cross-instance sync (optional):** if the mutation must replicate to other
   players in an ECS room, add a wire event: hook in `EcsNet/EcsHooks.cs`, an
   `OnLocal*` publisher + `ApplyEvent` case in `EcsNet/EcsNetManager.cs`, message
   builders in `EcsNet/Protocol.cs` **and its hand-synced twin
   `worker/src/protocol.ts`**, plus an e2e assertion (see `docs/ecs-systems.md`
   for the per-system spec format).
5. **Event push (optional):** to notify subscribers asynchronously, build a
   `JObject` frame and call `HttpChannel.Broadcast(frame)` (internal; main
   thread only) — this is how `menu_choice` and `quest_complete` are emitted.
6. Document the verb here and, if rogue-gm should use it, note it in that repo.
