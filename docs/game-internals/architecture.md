# Streets of Rogue — Core Architecture

**What this covers / when to read it:** the game's skeleton — the `GameController` singleton and its managers, the per-floor scene-reload model, session/save/unlock persistence, spawning and object pooling, Rewired input, and the Mirror netcode authority model. Read this first before writing any mod code that touches live game state; every other game-internals doc assumes this one. For NPCs/combat see `agents-ai-combat.md`; for items/effects/unlocks-as-content see `content-systems.md`; for level generation, objects, and menus see `world-and-ui.md`; for sprites/audio/text see `sprites-audio-localization.md`.

> All `decompiled/...` paths refer to the decompiled game source, which is **gitignored** and exists only in the main checkout at `~/Projects/streets-of-rogue-multiplayer/decompiled/` (652 top-level `.cs` files; 2,937 in the whole tree). Line numbers are anchors from the current decompile, not exact contracts.

The architecture in one sentence: **one god-singleton (`GameController`) + string-keyed switch statements + a full scene reload per floor + Mirror netcode where the host is authoritative.**

---

## 1. GameController (`gc`) — the central singleton

`decompiled/GameController.cs` (4,643 lines), `public class GameController : MonoBehaviour` (line 15).

- Static instance: `public static GameController gameController;` (line 17), assigned first thing in `Awake()` (line 1023).
- **Canonical access idiom:** nearly every class caches it as `gc = GameController.gameController;` in its own `Awake`/`RealAwake` (this exact line appears in 212 files). Mods should follow the same pattern.
- **Gotcha:** anything that runs before `GameController.Awake` sees `gameController == null`. And because each floor reloads the scene (§2), *GC and every manager on it are recreated per floor* — never cache `gc` or a manager across floors in a static field without re-resolving.

### Managers it owns

Most managers are components on the `ScriptObject` GameObject (`GameObject.Find("ScriptObject")`, line 1024), wired in the `Awake` block at lines ~1128–1437. The ones a modder actually uses (declaration line in parentheses):

| Field | Type / role |
|---|---|
| `loadLevel` (585) | `LoadLevel` — level generation & floor flow (see `world-and-ui.md`) |
| `spawnerMain` (613) | `SpawnerMain` — runtime spawning (§4) |
| `playerControl` (583) | `PlayerControl` — Rewired input (§5) |
| `sessionData` (643) / `sessionDataBig` (645) | per-level / cross-floor persistent state (§3) — **separate `DontDestroyOnLoad` GameObjects, not on ScriptObject** (found by name at lines 1130–1131) |
| `unlocks` (647) | `Unlocks` — meta-progress registry (§3) |
| `saveGame` (633) | `SaveGame` — run save/load (§3) |
| `updater` (667) | `Updater` — **the real per-frame driver** (§2) |
| `gameResources` (619) | `GameResources` — string→prefab/sprite databases, `DontDestroyOnLoad` (see `sprites-audio-localization.md`) |
| `audioHandler` (615) | `AudioHandler` — string-keyed sound playback |
| `poolsScene` (649) | `PoolsScene` — object pools (§4) |
| `quests` (611), `stats` (653), `dialogueBox` (655), `tutorial` (659) | quest system, run stats, dialogue UI, tutorial |
| `networkManagerUWP` (577), `networkManagerB` (579) | Mirror netcode (§6) |
| `mainGUI` (705), `menuGUI` (713), `worldSpaceGUI` (715), `minimap` (747), `cameraScript` (685) | GUI roots (see `world-and-ui.md`) |
| `nameDB` (via `Google2uDatabase` components, lines 1429–1437) | localization/name lookup: `AgentNames`, `ItemNames`, `ObjectNames`, `StatusEffectNames`, `InterfaceNames`, `DialogueNames`, `DescriptionNames`, `UnlockNames` |

### Player references

- `public Agent playerAgent;` (409) — the **local** main player; `playerAgent2/3/4` for local co-op; `hostPlayer` (829).
- `public List<Agent> playerAgentList` (121) — all players (includes remote in multiplayer).
- `gc.playerAgent.objectMult` is the network mouthpiece — code all over the game calls `gc.playerAgent.objectMult.Cmd…` to request host-side actions (§6).

### The world-state registries

"Everything currently in the level" lives in public collections on GC (lines ~419–575). Iterate these to find things:

- `Dictionary<int, PlayfieldObject> playfieldObjectDic` (421) — every networked playfield object by UID.
- `Dictionary<int, Agent> agentDic` (423) + `List<Agent> agentList` (427); also `activeBrainAgentList` (431), `agentFixedUpdateList` (433), `deadAgentList` (439).
- `Dictionary<int, Item> itemDic` (453) + `itemList` (455) — **note:** this is the *world pickup* registry (`Item` MonoBehaviours), unrelated to `GameResources.itemDic` (sprites).
- `Dictionary<int, Bullet> bulletDic` (443); `List<ObjectReal> objectRealList` (479) with filtered sublists (`objectRealListUpdate`, `…LateUpdate`, `…Animator`); `chestDic`/`chestList`; `noiseDic`; `spawnerDic`; `firesList`, `gasesList`, `dangerList`, `windowList`, `holeList`, etc.

**Gotcha:** agents/items exist in *both* a Dictionary (int UID key) and a List, and the game keeps them in sync inside `SpawnerMain`/`PoolsScene`. Never add/remove from these collections directly — spawn and despawn through the official paths (§4).

GC itself does almost no per-frame work: its `Update()` (4636) only increments `timeSinceLoad`; `LateUpdate()` (4631) only calls `SetTimeScale()`. The per-frame engine is `Updater` (§2).

---

## 2. Lifecycle: scenes, startup, floors, frames

### The two-scene model (the single most important mental model)

Only two scenes are ever loaded by name: `"MainGame"` and `"BetweenLevels"` (`LoadLevel.LoadScene(string)`, `decompiled/LoadLevel.cs:13437`). **Every floor is a full reload of `MainGame`**, so `GameController.Awake` re-runs on every floor and all level objects are destroyed and recreated. Only `DontDestroyOnLoad` singletons survive: `SessionData`, `SessionDataBig`, `GameResources`, `AudioHandler`, `ChunkLoad`, `SteamManager`. **Run-scoped mod state must live on (or parallel to) `SessionDataBig`, never on GC or level objects.**

### Startup chain

`GameController.Awake()` (1019) → `Awake2()` coroutine (1771) → `Awake3()` (1845):

1. `Awake` (1019): sets the static instance; finds `ScriptObject`; instantiates/finds `GameResources` and calls its `RealAwake()` + `SetupDics()` (1026–1039); instantiates `ChunkLoad`/`AudioHandler` if absent (1049–1102); instantiates the A* pathfinding prefab (1116–1123); resolves platform flags; wires all manager refs (1128–1437).
2. `Awake2` (1771): waits for settings load; loads unlock data (`unlocks.LoadUnlockData`, 1824).
3. `Awake3` (1845): language/fonts, copies user settings out of `sessionDataBig`; then the menu-vs-game branch (1985): if `!sessionData.gotData && !startedGame` → title-screen path (`menuGUI.titleScreen = true`, `CreateMainPlayerAgent()`, …); otherwise wires the network manager and calls `networkManagerUWP.StartNewLevel()` (2027).

### Entering / leaving a floor

- On each `MainGame` scene load, `LevelTransition.ChangeLevel()` (`decompiled/LevelTransition.cs:17`) is the orchestrator. It resets the local **ID allocators** (`playfieldObjectCount = 16`, `agentCount = 16`, `bulletCount = 0`, `itemCount = 0`, `objectRealCount = 0`, ~lines 37–44) and, at its end (`LevelTransition.cs:1440`), either returns to the main menu or calls `gc.CreateMainPlayerAgent()` + `StartCoroutine(gc.WaitForRealStart())` (1449); in multiplayer, `networkManagerB.StartNewLevel()` / host `AddPlayersNewLevel()`.
- `GameController.WaitForRealStart()` (2348) waits for `poolsScene.poolLoaded` (2368), then instantiates the player agent and calls `CreateMultPlayerAgent()` (2379) → `AwakenObjects()` (2493). In multiplayer the equivalent trigger comes from `ObjectMult` (`gc.AwakenObjects()` at `decompiled/ObjectMult.cs:23550`).
- Going down the elevator: `LoadLevel.NextLevel()` (`decompiled/LoadLevel.cs:12288`) — guards `switchingLevel`, per-player `sessionData.Store(...)` (12389/12398), `ResetStatics()` (13074), curtains, timescale zero. `LoadLevel2()` (12410) runs `saveGame.Save()` (skipped for HomeBase/Tutorial, 12422) then `IncreaseLevel()` (12185, bumps `sessionDataBig.curLevel`/`curLevelActual`/`curLevelEndless`; Endless wraps 15→1). Revive path: `ContinueGame()`/`ContinueGame2()` (12210/12245). Quit/restart: `QuitToMainMenu()` (13024), `RestartGame()` (12604).
- Multiplayer floor changes barrier on everyone: `WaitForEveryoneToSelectCharacter` / `WaitForEveryoneToFinishLevel` (`LoadLevel.cs:12435/12151`).

### The per-frame loops — `Updater.cs`, not GameController

`decompiled/Updater.cs` (755 lines) drives everything:

- `FixedUpdate()` (19): if `!gc.levelTransitioning` → `FixedUpdateAgents()` (27), iterating `gc.agentFixedUpdateList` calling `agent.AgentFixedUpdate()`, `agent.combat.FixedUpdateCombat()`, `agent.pathfindingAI.FixedUpdatePathfindingAI()` — each wrapped in try/catch.
- `Update()` (49): guarded by `!gc.levelTransitioning && !gc.offlineGameStarting`; runs `UpdateAgents()` (83), `UpdateObjectReals()` (301), `UpdateItems()` (337), `UpdateOther()` (383), `UpdateInterface()` (450). `UpdateAgents` alternates an `onCameraFrame` 0/1 flag to spread `AgentOnCamera()` work across frames.
- `LateUpdate()` (476) → the matching `LateUpdate*` passes.
- **AI is time-sliced**, not per-frame: `LoadLevel.UpdateAIOffsetGroups()` (`LoadLevel.cs:13460`) cycles agents through 6 offset groups (~one group per 0.0167 s), calling `agent.brainUpdate.MyUpdate()`, `combat.CombatCheck()`, `pathfindingAI.UpdateTargetPosition()`. Don't assume an agent's brain runs every frame.
- **Gotcha:** every per-frame agent call is wrapped in try/catch that just `Debug.LogError`s. A throwing mod hook won't crash the game — it will silently log-spam and the agent will stop updating properly.

---

## 3. Persistence: SessionData, SessionDataBig, SaveGame, Unlocks

Four distinct stores with different lifetimes:

### `SessionDataBig` — cross-floor / cross-session run state
`decompiled/SessionDataBig.cs` (730 lines), `DontDestroyOnLoad` at line 481. Holds the run: `curLevel` (34), `curLevelEndless` (36), `curLevelActual` (38), `nextLevelType` (362, string: `"Normal"`/`"HomeBase"`/`"Attract"`), `challenges`/`originalChallenges` (mutator lists, mirrored into `gc.challenges` at `GameController.cs:1301`), custom-character `customData1..4`, `characterStartingItems1..4`, `joystickAssignments`, language, and all graphics/audio settings. **This is where run-scoped mod state belongs conceptually** — GC fields reset every floor.

### `SessionData` — per-level heavy state
`decompiled/SessionData.cs` (2,715 lines), `DontDestroyOnLoad` at line 303. `Store(...)` (called per player in `NextLevel`, `LoadLevel.cs:12389/12398`) serializes each player's inventory/status so the next floor's freshly spawned player agent can be rehydrated. `ClearNPCData()` runs on every `NextLevel` (`LoadLevel.cs:12360`). Its `gotData` flag (set in `ContinueGame2`) is what makes `Awake3` restore a game instead of showing the menu.

### `SaveGame` — the on-disk run save
`decompiled/SaveGame.cs` (2,839 lines). `Save()` (883), `Load(bool secondTry, bool isMultiplayer)` (1600), client variants `LoadClient()` (2334)+. Validity: `IsValid()` (610), `Invalidate()` (477), `DepleteContinue()` (775). Saving is skipped for HomeBase/Tutorial floors.

### `Unlocks` — meta-progress, separate from the run save
`decompiled/Unlocks.cs` (3,972 lines). `LoadUnlockData()` (690) / `SaveUnlockData()` (419). Everything is keyed by `(unlockName, unlockType)` **string pairs**: query with `IsUnlocked(name, type)` (358), `IsUnlockedAndActive` (386), `GetUnlock(name, type)` (1237); mutate with `DoUnlock` (205), `DoUnlockProgress` (282+), `AddUnlock` (1163+), `AddNuggets`/`SubtractNuggets` (nuggets are the meta-currency). This registry is the main hook surface for modded characters/items/traits — see `content-systems.md` §5 for the full unlock model, and note RogueLibs' save-safety techniques in `../modding/roguelibs-techniques.md` before writing modded unlock state to disk.

---

## 4. Spawning and object pooling

### `SpawnerMain` — the authoritative runtime spawner
`decompiled/SpawnerMain.cs` (6,905 lines), `gc.spawnerMain`. Holds the prefab bank (agent prefabs at lines 18–22; ~60 bullet prefab variants with `_S/_M/_B` size suffixes, 24–160).

- **`SpawnAgent(...)`** — 12+ overloads (1603–1658) funneling into the 14-arg master at line 1658. **Critical gotcha (line 1660):** on a non-host client the method sends `CmdSpawnAgent(...)` and **returns `null`** — only the host actually instantiates. Agent type is a string (`"ObjectAgent"`, `"Custom"`, character names); spawned objects are named `agentType + " (" + UID + ")"` (1696).
- Other spawns: `SpawnBullet` (2658+), `SpawnExplosion` (3336–3366, string `explosionType`), `SpawnItem` (4007–4031), `SpillItem` (4122), `SpawnWreckage*` (4296–4468), `SpawnNoise` (4571+, string `noiseType`), `SpawnFloorDecal` (4535), `SpawnParticleEffect(effectType, …)` (5698+). Specials: `TransformAgent` (1988), `SpawnEnforcer` (2306), `SpawnButlerBot` (2450).

### `PoolsScene` — pools, and Mirror client spawn handlers
`decompiled/PoolsScene.cs` (3,601 lines), `gc.poolsScene`, on GameObject `"ObjectPool2"` (`GameController.cs:2365`). Pooling is gated by gc flags `objectPools`/`agentPools`/`activePooling` (`GameController.cs` fields 151–175).

- Agent pool: `SpawnAgent(agentName, spawnPosition)` (2589) — used by SpawnerMain when `gc.agentPools && playerColor == 0` (`SpawnerMain.cs:1678–1681`); `RemoveAgent` (2873), `ResetPoolAgent` (2969), `ResetAgent` (3128). Walls: `SpawnWall` (826)+. ObjectReals: `SpawnObjectReal(objectRealName, prefab, pos)` (2052), `ResetObjectReal` (2356), `RemoveObjectReal` (2553).
- Mirror client-side spawn handlers (registered with Mirror's spawn system): `SpawnAgentClient` (3437), `SpawnItemClient` (3463), `SpawnFactoryObjectClient` (3491), `SpawnFireClient` (3519), `SpawnGasClient` (3548), `SpawnObjectRealClient` (3575) + matching `UnSpawn*Client` — clients recycle from pools rather than instantiating.

**Pooling gotcha:** pooled objects are recycled, not destroyed. Unity `Awake`/`Start` do **not** re-run on reuse — the game calls `RecycleAwake()`/`RecycleStart()` instead (`SpawnerMain.cs:1688–1689`), and classes reset themselves via `RevertAllVars()`-style methods. If your mod attaches state to an agent/item/object, reset it in the recycle path or it leaks into the next thing that reuses the instance.

---

## 5. Input — Rewired

`decompiled/PlayerControl.cs` (4,612 lines), `using Rewired;`. `gc.playerControl`.

- Rewired players: `Player systemPlayer` (429), `Player[] rewiredPlayer` (431, size 4), fetched in `Awake()` via `ReInput.players.GetPlayer(0..3)` (504–507). Controller assignment in `Start()` (1042) reads `gc.sessionDataBig.joystickAssignments[]` and toggles keyboard/gamepad maps with `SetMapsEnabled(...)`.
- **Input is read once per frame in `Update()`** (1900) into parallel per-player `bool[]` triples: `pressedAttack[]/releasedAttack[]/heldAttack[]` (49–55) and the same for Cancel, SpecialAbility, Interact, Inventory, UseItem, Item1..5, Menu, etc. (49–159+). Movement axes: `heldAxisX[i] = rewiredPlayer[i].GetAxis("MoveXJ")` / `"MoveYJ"` (2100–2101) plus keyboard `MoveLeftK/RightK/UpK/DownK` (2109–2121). Actions are read via per-player **string action names** (`attackStr[i]`, `interactStr[i]`, …) with `GetButtonDown/GetButton/GetButtonUp` (2148+).
- Downstream consumption: the player's `Agent`/`AgentInteractions` read these arrays each frame. Useful entry points for mods: `keyCheck(buttonType, Agent)` (3893), `keyCheckHeld` (4118), `pressButtonDown(string, Agent)` (3826), `Vibrate(playerNum, intensity, seconds)` (4225), and the transition lockout `SetCantPressGameplayButtons("All", 1, 0)` (1728, called during every level transition).
- **Gotchas:** input arrays are only meaningful for `localPlayer` agents — remote players' actions arrive as network Commands, not through PlayerControl. Also note the game reads *some* gun-fire input through `keyCheck`/Rewired directly rather than the arrays; EightPlayers' `VirtualInput.cs` documents this limitation (it can move agents but not fire weapons by overwriting the arrays).

---

## 6. Networking — Mirror, host authority, and the ObjectMult family

The vanilla game ships Mirror. This decompile is of the multiplayer build; the classes below are vanilla, not mod additions.

### Managers
- **`NetworkManagerUWP`** (`decompiled/NetworkManagerUWP.cs`) — the actual `Mirror.NetworkManager` subclass (line 9). Overrides `OnServerConnect` (63), `OnClientConnect` (93), `OnServerAddPlayer` (290, → coroutine chain ending in `NetworkServer.AddPlayerForConnection`), `OnServerSceneChanged` (848), `OnServerDisconnect` (867), etc. Sets `gc.serverPlayer = true` on the host path (lines 364, 853). `StartHost()` (1483), `StartClient()` (1504).
- **`NetworkManagerB`** (`decompiled/NetworkManagerB.cs`) — a thin facade delegating to `networkManagerUWP` (e.g. `StartHost()` line 85). Holds `List<Agent> agentListSolid` (16) — pre-allocated per-player-color agent slots that SpawnerMain reuses (`SpawnerMain.cs:1682–1690`).

### Per-object netcode
Every `PlayfieldObject` caches its `ObjectMult objectMult` (`decompiled/PlayfieldObject.cs:41`, assigned at 1399). Hierarchy: `ObjectMultPlayfield : NetworkBehaviour` (`ObjectMultPlayfield.cs:7`) → `ObjectMult` (`decompiled/ObjectMult.cs:12` — 25,693 lines, the largest gameplay file, with **~425 `[Command]/[ClientRpc]/[TargetRpc]` methods**) → `ObjectMultAgent` (agents) and siblings `ObjectMultItem/Object/Fire/Gas/Hole/Generic`.

Naming convention is strict: `Cmd*` = client→server, `Rpc*` = server→all, `Target*` = server→one client; NPC-driven variants take a leading `uint myAgentID` (e.g. `CmdAttackMeleeObject` at 1354 vs `CmdNPCAttackMeleeObject` at 1365). Cross-machine references are passed as `uint` net IDs and looked up in `agentDic`/`playfieldObjectDic`, never as object refs.

### The host-authority rules (modders must respect these)

The host (`gc.serverPlayer == true`) is authoritative for essentially all world mutation. The pervasive pattern: *if not server, send a `Cmd…` and return; the server acts and broadcasts an `Rpc…`.*

1. Guard world-changing mod code with `gc.serverPlayer`; route client requests through a `Cmd` on `gc.playerAgent.objectMult`. Client-side direct mutation is *silently wrong* (e.g. `SpawnAgent` returns `null` on clients, §4).
2. Never `Instantiate` gameplay objects directly — go through `SpawnerMain`/`PoolsScene` so IDs and Mirror spawn handlers stay consistent.
3. Adding brand-new `[Command]`/`[ClientRpc]` methods requires Mirror's build-time weaving — impractical in a runtime Harmony mod. Piggyback on existing string-parameterized RPCs, run host-only logic, or use an out-of-band channel (EightPlayers' EcsNet does the latter).
4. `PlayfieldObject.UID` (int, line 9) and `objectNetID` (uint, line 25) are different ID spaces; the `…Count` allocators reset each floor in `LevelTransition.ChangeLevel` (§2).

---

## 7. Conventions and where to grep

### String IDs + giant switches
All content behavior is keyed by strings dispatched through enormous switch statements (case counts): `StatusEffects.cs` **756**, `Quests.cs` 325, `AgentInteractions.cs` 241, `InvItem.cs` 235, `Agent.cs` 192, `ItemFunctions.cs` 64. The big dispatchers: `ItemFunctions.UseItem` (`decompiled/ItemFunctions.cs:16`, switch on `item.invItemName`), `InvItem.SetupDetails` (`decompiled/InvItem.cs:423`, per-item stats/flags), and their peers in `Agent.cs`/`StatusEffects.cs`/`AgentInteractions.cs`. Details in `content-systems.md`.

### Lookup databases
- `GameResources` (`decompiled/GameResources.cs`, 1,033 lines, `DontDestroyOnLoad` at 85): `wallPrefabDic`/`objectPrefabDic` (`Dictionary<string, GameObject>`, lines 14/18), `itemDic`/`objectDic` (`Dictionary<string, Sprite>`, 42/46) and body/hair/eyes dicts — populated by `SetupDics()` from parallel lists. See `sprites-audio-localization.md` for the crucial UI-vs-tk2d split.
- `NameDB` components on the `Google2uDatabase` GameObject — display text by `(name, category)`.
- `InvDatabase` (`decompiled/InvDatabase.cs`, 5,922 lines) — the per-agent inventory component.

### Naming patterns to grep for
- `RealAwake()`, `RealStart()`, `RecycleAwake()`, `RecycleStart()`, `RevertAllVars()`, `SetupDics()` — the game's replacement lifecycle for manually instantiated/pooled objects. **Do not assume Unity `Awake`/`Start` fire when you expect.**
- Network methods: `Cmd*`, `Rpc*`, `Target*` prefixes (NPC variants insert `NPC`).
- Level flow: `SetupMore`, `NextLevel`, `ChangeLevel`, `WaitForRealStart`, `AwakenObjects`. Spawning: grep `Spawn` in `SpawnerMain.cs`/`PoolsScene.cs`.
- Ignore `UnitySourceGeneratedAssemblyMonoScriptTypes_v1.cs` (26,542 lines of IL2CPP metadata, not gameplay).

---

## 8. Cross-cutting gotcha checklist

1. **Per-floor scene reload** wipes GC and all level objects; only the `DontDestroyOnLoad` singletons persist. Put run state in/alongside `SessionDataBig`.
2. **Host authority:** clients must go through `Cmd*`; client-side direct mutation silently no-ops or desyncs.
3. **Object pooling:** recycled objects skip Unity `Awake`/`Start`; hook `RecycleAwake`/`RecycleStart` and reset custom state in the revert paths.
4. **String-keyed switches:** adding content vanilla-style means touching several switches plus `GameResources`/`NameDB`/`Unlocks` registrations — this is exactly what Harmony patches (and formerly RogueLibs) exist to avoid; see `../modding/`.
5. **Time-sliced AI** (6 offset groups) and camera-frame-split agent updates — brains don't tick at framerate.
6. **Swallowed exceptions:** the per-frame loops try/catch everything into `Debug.LogError`; a broken hook degrades silently. Watch the BepInEx console/log while developing.
