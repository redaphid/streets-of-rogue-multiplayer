# Streets of Rogue — World, Objects, and UI

**What this covers / when to read it:** how a floor gets generated (LoadLevel, themes, chunks, the tile grid), how interactive world objects work (the `ObjectReal` hierarchy and its virtual-method contract), how object types are registered by string name, and how the menu systems (`MainGUI`, `ScrollingMenu`, `CharacterSelect`) are built and enumerate content. Read it when your mod places things in the world, adds/changes an interactive object, or touches menus/portraits. Prerequisite: `architecture.md` (the gc singleton, scene reloads, pooling, host authority). Sprites/audio/text rendering details live in `sprites-audio-localization.md`; NPC behavior in `agents-ai-combat.md`; items/effects/unlocks in `content-systems.md`.

> All `decompiled/...` paths refer to the decompiled game source, which is **gitignored** and exists only in the main checkout at `~/Projects/streets-of-rogue/multiplayer/decompiled/`. Line numbers are anchors, not contracts.

---

## 1. Level generation

### Orchestration — `decompiled/LoadLevel.cs` (13,509 lines)

Generation runs as a staged coroutine/Invoke chain after the `MainGame` scene loads (see `architecture.md` §2 for how we get here):

`LoadLevel.Start()` (333) → `SetLevelTheme()` (2113) → `SetupBasicLevel()` (2452) → `SetupMore()` coroutine (2528) → `Invoke("SetupMore2", 0.1f)` (2605) → `SetupMore2()` (2770) → `SetupMore3()` (3295) → `SetupMore3_2()` → `SetupMore4()` (6611) → `SetupMore5()` (7257).

Placement helpers inside the chain: `PlaceChunk()` (9326), `PlaceConnector()` (9446), `CanUseChunk()` (11527); population passes: `SetupRels()` (7065), `SpawnEmployedAgents()` (8052), `HomeBaseAgentSpawns()` (7819), `SpawnCopNearLocation()` (7136). The chunk list is loaded by `LoadChunkListBasic()` (388).

### Level type and theme

- **`gc.levelType`** (string): `"Normal"`, `"HomeBase"`, `"Tutorial"`, `"Attract"` (set around `LoadLevel.cs:718–1039`). Many systems branch on it — e.g. saves are skipped for HomeBase/Tutorial, and `SpawnerMain.SpawnAgent`'s client guard exempts `"HomeBase"`.
- **`gc.levelTheme`** (int) — the district/biome: `0` = Slums, `1` = Industrial, `2` = Park/Outdoor, `3` = Downtown, `4` = Uptown, `5` = Mayor Village. (Mapping confirmed at `decompiled/ItemHitbox.cs:148–171` and used throughout `LoadLevel.cs:1984–2045`.) Theme drives wall/floor sprite variants and building selection; HomeBase/Tutorial force theme 0.

### The tile grid — `decompiled/TileInfo.cs`

The world is a 2D grid of `TileData`: `TileInfo.tileArray = new TileData[levelSizeAxis*16, levelSizeAxis*16]` (`TileInfo.cs:198`), i.e. 16 tiles per chunk-axis unit, with parallel arrays `tileArrayOriginal`, `tempTileData[32,32]` (per-chunk scratch, 206), and reachability scratch (114–132). `TileData` holds position, `chunkID`, wall material, and overlap flags — it doubles as the **pathfinding/walkability grid** consumed by movement and AI. Useful point queries: `TileInfo.IsOverlapping(pos, "Hole"/"Water")`, and `FindLocationNearLocation(...)` for "give me a valid nearby position" (used by teleport-style effects). Walls are built through `TileInfo.BuildWallObject(...)` (called from `LoadLevel.cs:2100`) using `GameResources.wallPrefabDic`.

### Chunks

A chunk is a hand-authored building/room template: `decompiled/Chunk.cs`, `ChunkData.cs`, `ChunkPackData.cs`, `ReadChunks.cs`, loaded via `ChunkLoad`/`ChunkLoadBasic`. Chunks are matched by `chunkName` (`LoadLevel.cs:435`, 1214, 1222); `gc.staticChunk` forces a specific one. Large streaming levels use `StreamingWorld.cs`/`StreamingTilemaps.cs`/`StreamingTileArray.cs`.

### How things get placed

- **Objects:** `LoadLevel.cs:1404` iterates the chunk's object list and calls `gc.poolsScene.SpawnObjectReal(name, gr.objectPrefabDic[name], Vector3.zero)` — object identity is the string key into `objectPrefabDic` (§3). Runtime plumbing: `decompiled/SpawnerObject.cs:70–75`.
- **NPCs:** `decompiled/SpawnerAgent.cs` + `SpawnerMain.SpawnAgent...`; who spawns where is driven by the `RandomSelection` weighted-list system (`decompiled/RandomSelection.cs`, `randomListTable`) plus chunk metadata. NPC appearance (hair, skin) also flows through `RandomSelect` (see `sprites-audio-localization.md`).

---

## 2. Objects — the `ObjectReal` hierarchy

### Base class contract — `decompiled/ObjectReal.cs` (3,532 lines), extends `PlayfieldObject`

Identity: `objectName` (string, inherited from `PlayfieldObject`, set on the prefab) is the canonical identity; display text resolves via `objectRealRealName = gc.nameDB.GetName(objectName, "Object")` and `description = GetName(objectName, "Description")` (`ObjectReal.cs:1056–1057`). Other data: `prefabName` (18), `objectType` (302), `direction` (334), `startingObjectSprite` (490), sound overrides `specialDestroySound`/`specialDamageSound` (146–150).

The virtual methods a subclass (or a Harmony patch) overrides — this is the whole "interactive object" contract:

| Method | Role |
|---|---|
| `Interact(Agent)` (1447) / `InteractFar(Agent)` (1453) / `InteractImmediate` | player walked up and pressed interact (near/far/instant). Base caches `playerInvDatabase`. |
| `DetermineButtons()` / `PressedButton(string)` | build the context-menu button list / handle a chosen button |
| `UseItemOnObject(InvItem, slotNum, combineType, useOnType)` (1330) | an item was used on this object (keys, hacking tools, …) |
| `Damage(PlayfieldObject[, bool fromClient])` (1460+) → `DamagedObject(PlayfieldObject, float)` | damage entry → per-subclass reaction. Sentinel `9999` = no damage. |
| `DestroyMeGeneric(damagerObject, causerAgent)` (1579) | destruction; spawns wreckage via `gc.spawnerMain.SpawnWreckagePileObject(tr.position, objectName, burnt)` (1153) |
| `ObjectAction(myAction, extraString, extraFloat, causerAgent, extraObject)` (1355) | generic string-keyed action dispatch (also the network entry for object actions) |
| `MakeNonFunctional(PlayfieldObject)` / `FinishedOperating()` | disable / operation-complete callbacks |
| `ObjectCollide`/`ObjectCollideStay`/`ObjectExit`/`ObjectCollideWall`/`Trap` (1335–1351) | collision hooks |
| `RecycleAwake` (907), `RevertAllVars` (586), `SaveWorldData`/`LoadWorldData` | pooling lifecycle + per-floor persistence — **reset custom state here** (see `architecture.md` §4 gotcha) |

The in-world visual is a child `tk2dSprite` managed by `decompiled/ObjectSprite.cs` (`spr` main + `sprH` highlight, lines 29–31) — sprite mechanics in `sprites-audio-localization.md`.

### Two worked examples

- **`decompiled/Safe.cs` (307 lines) — the clean template.** `class Safe : ObjectReal` overriding exactly the contract: `Interact` (91), `MakeNonFunctional` (112), `DetermineButtons` (122, adds buttons), `PressedButton` (158), `UseItemOnObject` (209, safe-cracking items), `playerHasUsableItem` (241), `FinishedOperating` (256), `DamagedObject` (297). Read this file first when writing a new object behavior.
- **`decompiled/Door.cs` (3,229 lines) — the kitchen sink.** Overrides `Interact` (1087), `InteractFar` (1078), `InteractImmediate` (1200), plus lock logic and cross-floor state via `SaveWorldData`/`LoadWorldData` (379/387). Note doors are identified positionally by mods (UIDs drift across clients — EightPlayers' `GameStateApi.FindDoorAt` quantizes position for this reason).

Other ready-made subclasses to crib from: `ATMMachine.cs`, `SecurityCam.cs`, `PowerBox.cs`, `Computer.cs`, `Generator.cs`, `SawBlade.cs`, `Bed.cs`.

### Object registration by string name

`GameResources.SetupDics()` (`decompiled/GameResources.cs:104–180+`) builds `objectPrefabDic["Name"] = objectPrefabList[i]` — a hardcoded, index-ordered map of ~90+ object types (`AirConditioner`, `AlarmButton`, `Altar`, …, `Door`, …, `Refrigerator`, `Safe`, …). **Adding a new object type** means providing a prefab and injecting it into `objectPrefabDic` (a postfix on `SetupDics` is the natural seam), plus tk2d sprites for the world visual, `objectDic` for the UI icon, and `NameDB` `"Object"`/`"Description"` rows — the full recipe is in `../modding/` and the asset half in `sprites-audio-localization.md`. Interaction buttons can also be added to *existing* objects purely via Harmony on `DetermineButtons`/`PressedButton` (RogueLibs built an entire interaction framework on exactly those two seams — see `../modding/roguelibs-techniques.md`).

---

## 3. UI and menus

### `MainGUI` — the in-game HUD root

`decompiled/MainGUI.cs` (6,194 lines). Key member: `public InvInterface invInterface` (103) — the inventory/HUD interface, reached per-agent as `agent.mainGUI.invInterface`. The HUD is assembled from sibling components, each its own file: `InvInterface.cs` (inventory grid), `InvSlot.cs` (one slot), `EquippedItemSlot.cs` (weapon/armor/**special-ability** slots), `BuffDisplay.cs` (status-effect indicators), `AgentHealthBar.cs`, `SpecialAbilityIndicator.cs`, `BigMessage.cs`. All of these render item icons via UnityEngine.UI `Image.sprite` from `GameResources` dictionaries — *not* tk2d (the distinction that broke WizardMod's assets; see `sprites-audio-localization.md`).

`decompiled/MenuGUI.cs` (16,460 lines) is the title screen / main menu; `menuGUI.titleScreen` gates a surprising amount of behavior (music, input, the Awake3 menu-vs-game branch).

### `ScrollingMenu` — menus are data-driven from Unlocks

`decompiled/ScrollingMenu.cs` (5,009 lines). `OpenScrollingMenu()` (631) builds every scrolling list (mutators, item unlocks, traits, loadouts, rewards…) by **enumerating `Unlock` lists held on `gc.sessionDataBig`** (`challengeUnlocks`, `agentUnlocks`, `bigQuestUnlocks`, …): `numButtons = challengeUnlocks.Count + 1` (711), sorted by `SortUnlocks(list, "Challenge")` (712, def 527). Mutator incompatibility filtering is a hardcoded block at 411–479 (e.g. `"HardToShoot"` hidden when `NoGuns` is active). Button labels/tooltips route through `NameDB.GetName(unlockName, "Unlock")` / `"Description"`.

**The practical consequence:** to make modded content appear in menus you don't touch ScrollingMenu at all — you register an `Unlock` of the right `unlockType` (see `content-systems.md` §5) and the menus enumerate it automatically. You only patch ScrollingMenu for new *menu behavior* (RogueLibs patched `OpenScrollingMenu` + the `Setup*` family to add custom buttons and paging).

### `CharacterSelect` — the roster and portraits

`decompiled/CharacterSelect.cs`:

- The roster is `slotAgentTypes` (48), built in `RealAwake()` (238, entries added at 242–308: Hobo, Soldier, Gangbanger, Thief, …, Vampire, …, GangbangerB); `slotAgentTypesComplete` (50) includes locked/DLC entries. Slots ≥ 32 are the "Create Character" custom slots — which is why WizardMod and the character-creator mod displace a built-in (e.g. `GangbangerB`) or claim a slot index rather than appending freely.
- **Portraits are layered UnityUI `Image`s reading GameResources dictionaries, keyed by name + the `"S"` (south/front-facing) suffix:**
  - Body: `slotAgent[n].transform.Find("Body").GetComponent<Image>().sprite = gr.bodyDic[agentName + "S"]` (`CharacterSelect.cs:1502`, also 1852, 2266); Gorilla-family bodies read `gr.bodyGDic` (1490, 2261).
  - Hair `gr.hairDic[type + "S"]` (1534), facial hair `gr.facialHairDic` (1598), eyes `gr.eyesDic` (1718), head piece `gr.headPiecesDic` (1733).
  - The same layered-portrait pattern recurs in `CharacterCreation.cs:4698`, `QuestSlot.cs:1063/1067`, `DialogueBox.cs:198/202` (which uses the `"SE"` facing), and `LevelEditor.cs:7761`.
- **Gotcha:** these portrait Images are *flat, untinted sprites*. In-world body tinting (skin/hair/legs colors) happens in a separate tk2d color pass (`decompiled/ObjectSprite.cs:1208–1363`) and never applies to portraits — so a custom character that aliases another character's `bodyDic` entry will look like an untinted copy of that character on the select screen even if it's correctly tinted in-game. Details and the WizardMod diagnosis: `sprites-audio-localization.md`.
