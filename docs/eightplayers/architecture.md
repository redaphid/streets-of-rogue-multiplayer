# EightPlayers — module architecture

One BepInEx 5 / HarmonyX plugin (net472, GUID `com.hypnodroid.eightplayers`)
carrying two feature families: (1) the original 8-player/LAN/controller layer,
(2) a live debug-harness + AI-game-master command surface and an ECS-over-
Cloudflare netcode layer. This doc is the map; per-verb detail is in
[command-channel.md](command-channel.md), per-netcode-system detail is in
[../ecs-systems.md](../ecs-systems.md).

## Bootstrapping — `EightPlayers/Plugin.cs`

`EightPlayersPlugin.Awake()` binds all config (General, Controllers, `[EcsNet]`,
`[Tracing]`), runs `harmony.PatchAll`, installs `Tracing.NetTrace`, calls
`JoystickBinding.Init()`, adds the `EcsNet.EcsNetManager` component, and starts
`HttpChannel`. `Update()` is the **central per-frame pump** — every module gets
ticked from here rather than owning its own MonoBehaviour:
`JoystickBinding.Tick / ZeroTwoMapping.Tick / ControllerDebug.Tick /
CommandChannel.Tick / HttpChannel.Tick / BehaviorEngine.Tick (guarded) /
Labels.Tick / StoryQuests.Tick / GameStateApi.PinTick / LoadWatchdog.Tick /
Trace.Tick`. If you add a module that needs per-frame work, register it in this
pump.

## The 8-player / LAN patch set (also in Plugin.cs)

- `NetworkSlots_Patch` — postfix `NetworkManagerUWP.RealAwake`: pads every
  per-slot list to MaxPlayers (configurable, up to 16), raises `maxConnections`.
- `ServerFull_Patch` — **transpiler** on `NetworkManagerUWP.WaitUntilLoadComplete`
  (enumerator MoveNext): rewrites the two hardcoded `4`s.
- `PlayerLimitButton_Patch` — prefix `MenuGUI.PressedIncreasePlayerLimit`.
- `LanMenu_Patch` — postfix `MenuGUI.RealAwake`: un-hides the LAN menu button.
- `ForceSeed_Patch` — prefix/postfix `LoadLevel.loadStuff`: `SOR_SEED` /
  adopted-seed determinism forcing + partial `randomListTable` repair. This is
  one of the two "hack piles" `docs/ecs-migration-plan.md` plans to delete.
- `NoSteamFallback*` / `NoGalaxy*` patches — let a second game window launched
  outside Steam run platform-less instead of crash-looping in the GOG fallback.
- `StatusDisplayLoadGuard_Patch` — finalizer on `StatusEffectDisplay.RealStartB`
  (level-load-wedge fix).

## The Core/patch pair pattern

Feature modules split into a Unity-free `*Core.cs` (unit-tested in
`EightPlayers.Tests/`) and a thin patch/integration file. When adding a feature,
follow this shape.

| Module | Files | What it does |
|---|---|---|
| **GameStateApi** | `GameStateApi.cs` | The shared mutation surface. Every write goes through a **vanilla choke point** (SpawnerMain, StatusEffects, Door...) so it traces and takes real game paths. Find/resolve helpers (`FindAgent`, `ResolveUid`, player aliases), spawn/health/items/doors/walls/fire/gas, `SetGoal` (real brain goals via `BrainUpdate.SwitchGoal`), `Pin/PinTick` (per-frame position lock), `WorldHash()` (FNV-1a door-geometry fingerprint), `InventoryJson`/`NearbyJson`. Both CommandChannel verbs and the EcsNet apply-path call it. |
| **CommandChannel** | `CommandChannel.cs` | Verb dispatch (~90 verbs, one `switch`) + the file transport. See [command-channel.md](command-channel.md). |
| **HttpChannel** | `HttpChannel.cs` | Streaming-HTTP transport: `POST /cmd`, `GET /events` (NDJSON), `GET /state`; port discovery via `ep_port.txt`. Other modules push frames with `HttpChannel.Broadcast`. |
| **BehaviorEngine** | `BehaviorEngine.cs` | "Code mode": per-agent MoonSharp **Lua** scripts run `tick(api)` at ~10 Hz. HardSandbox, per-script instruction cap (AutoYieldCounter runaway protection), persistent `mem` table, auto-disable after 5 consecutive errors / 10 slow ticks. **Ships `MoonSharp.Interpreter.dll` alongside the plugin** — Plugin.cs guards against its absence. |
| **DialogueMenu** | `DialogueMenu.cs` + `DialogueMenuCore.cs` | Custom NPC talk menus / prefetched conversation trees. Patches `Agent.DetermineButtons` (replace population), `Agent.Interact`, `NameDB.GetName` (smuggle raw text past localization), `Agent.SayDialogue` (mute stock chatter), `Agent.PressedButton` (press → `menu_choice` event, canned reply, tree-level swap). |
| **StoryQuest** | `StoryQuest.cs` + `StoryQuestCore.cs` | GM story quests: registers a real `Quest` in `gc.quests.mainQuestList` (sentinel questType `EPStory`), marker via Labels, polled completion ~3 Hz, `quest_complete` event. Patches `QuestSlot.UpdateQuest` to draw raw objective text. |
| **MapBuilder** | `MapBuilder.cs` + `MapBuilderCore.cs` | Sculpts a base64 `.map` ASCII grid onto the live level via GameStateApi build/destroy verbs; returns `{anchors, built, bounds}` JSON. |
| **Label** | `Label.cs` + `LabelCore.cs` | World-space quest-marker text over any agent/object, via `Quests.CreateQuestMarker(..., "HomeBaseMarker", ...)` (the only always-visible, quest-free marker type). Smuggles text through a `NameDB.GetName` prefix (`EPLABEL::`). Self-pruning Tick. |
| **VirtualInput** | `VirtualInput.cs` | Programmatic gameplay input: overwrites `PlayerControl` held/pressed arrays AFTER `PlayerControl.Update` (postfix). **Cannot fire weapons** — see the limitation note in [command-channel.md](command-channel.md). |
| **ZeroTwoMapping / JoystickBinding** | `ZeroTwoMapping.cs`, `JoystickBinding.cs` | 8BitDo Zero 2 auto-layout; `SOR_PAD=N` binds one window to one gamepad. Both patch `PlayerControl.SetTitleControllers`/`SetInGameControllers` (postfixes). |
| **LoadWatchdog** | `LoadWatchdog.cs` | 45 s empty-level detector → bounded reload (`reloadlevel` verb). |
| **Reflect** | `Reflect.cs` + `ReflectCoerce.cs` | God-view reflection surface (`inspect/get/set/call/find/members/types/getmany/keys`), budget-capped output, WeakReference handle registry. |

## Tracing

- `Tracing/Trace.cs` — `SOR_TRACE=1` enables a JSONL behavior tracer
  (background-thread writer, `traces/trace-*.jsonl`). This trace is the parity
  oracle rogue-brain consumes and how e2e tests assert on mutations.
- `Tracing/GamePatches.cs` — the trace hooks + `NetTrace.Install`, which
  reflectively prefixes every Mirror `UserCode_*` body (~460) emitting `net/call`.
- Choke-point survey: `docs/trace-choke-points.md`.

## EcsNet — the Cloudflare netcode layer (overview)

Full per-system spec: **[../ecs-systems.md](../ecs-systems.md) (canonical)**;
architecture vision: [../ecs-netcode.md](../ecs-netcode.md). Mirror is not
involved — everyone runs a normal single-player game and the layer mirrors the
world through a Cloudflare Durable Object (one per room code, JSON over
WebSocket).

- `EcsNet/EcsNetManager.cs` — the MonoBehaviour hub: connection pump,
  join/welcome/snapshot, publishes local player entities at SendHz, applies
  remote spawn/set/despawn/event/world messages through GameStateApi.
  **NPC authority = lowest client id.** Seed claiming is first-write-wins;
  divergence is detected via `WorldHash` and healed (`HealWorldDivergence`).
  Harness surface (`HarnessSet/Get/Entities/Event`) backs the `ecs*` verbs.
- `EcsNet/EcsHooks.cs` — the Harmony hooks that publish local mutations (the
  same choke points the tracer observes): StatusEffects.ChangeHealth/
  AddStatusEffect/SetupDeath, LoadLevel.IncreaseLevel/SetupMore2,
  SpawnerMain.SpawnAgent/SpawnFire/SpawnGas, Door.OpenDoor/Lock/Unlock,
  ObjectReal.DestroyMe, Gun.spawnBullet, chest/shop takes, item drop/equip.
- `EcsNet/Protocol.cs` — C# twin of `worker/src/protocol.ts`. **These two files
  MUST stay hand-synced** — any message change edits both.
- `EcsNet/EcsWorld.cs` — typed ECS mirror + verbatim `Raw` JObject store (the
  harness's full-fidelity view; unknown components stay visible).
- Server: `worker/src/` (room.ts, ecs.ts SHARED/VOLATILE sets, protocol.ts,
  systems.ts) — the authoritative half; a JS client would target it.

## The second plugin: WizardMod

`WizardMod/` is a standalone plugin (GUID `com.hypnodroid.wizardmod`) — the
hand-written custom character (roster slot, stats, Chaos Magic ability, Big
Quest). Per-patch rationale: [../WIZARD.md](../WIZARD.md). Its asset-injection
shortcomings (why the icon can be blank and the portrait is an untinted
Vampire) are dissected in
[../game-internals/sprites-audio-localization.md](../game-internals/sprites-audio-localization.md).
The data-driven generalization of WizardMod lives in the separate
**character-creator** repo (below).

## Cross-repo map

| Repo | Role |
|---|---|
| `~/Projects/streets-of-rogue/multiplayer` | This repo: the Unity-side mods, the command channel, the trace oracle, and `decompiled/` (gitignored, main checkout only) |
| `~/Projects/streets-of-rogue/gm` | Live AI game-master; MCP bridge (`GameHost`) onto this repo's command channel. Primary consumer of the verb + events contract |
| `~/Projects/streets-of-rogue/brain` | Deterministic TypeScript rules sim; reads `decompiled/` for rules and consumes `SOR_TRACE` output as its parity oracle (see `docs/ecs-migration-plan.md` for the two-repo split) |
| `~/Projects/streets-of-rogue/character-creator` | Data-driven custom characters (`character.json`), generalized from WizardMod; the best-documented project — its docs style is the model for this tree |
| `~/Projects/streets-of-rogue/RogueLibs` | Fork of the discontinued community modding framework. **Read-only reference** — do not port to it; borrow its techniques (see [../modding/roguelibs-lessons.md](../modding/roguelibs-lessons.md)) |
