# RogueLibs — lessons and the patch treasure map

**What this covers / when to read it.** RogueLibs (fork at `~/Projects/streets-of-rogue/RogueLibs`, upstream discontinued Feb 2024, last release v3.6.9) was the community modding framework for Streets of Rogue. We keep the fork as a **read-only reference** — we borrow its techniques, we do **not** depend on it or port mods to it. Read this when you need to know *where* the game can be hooked for a feature (the treasure map below), how to inject tk2d sprites correctly, or how to touch unlock save data without corrupting saves. Paths like `RogueLibsCore/...` are relative to `~/Projects/streets-of-rogue/RogueLibs/`; game paths are relative to `decompiled/` in this repo's main checkout.

## The Cecil field-injection trick (and why Harmony can't do it)

RogueLibs ships a second DLL, `RogueLibsPatcher/RogueLibsPatcher.cs`, installed to `BepInEx/patchers/` (not `plugins/`). It's a **BepInEx preloader patcher**: it exposes `TargetDLLs = { "Assembly-CSharp.dll" }` and a `Patch(AssemblyDefinition)` method that runs **before the game assembly is loaded**, using **Mono.Cecil** to add new public non-serialized `System.Object` fields to game types:

- `__RogueLibsHooks` → `InvItem`, `PlayfieldObject`, `StatusEffect`, `Trait` (`RogueLibsPatcher.cs:46`)
- `__RogueLibsContainer` → `StatusEffect`, `Trait` (`:47`)
- `__RogueLibsCustom` → `ButtonData`, `Unlock`, `tk2dSpriteDefinition` (`:48`)

**Why:** Harmony can only rewrite *method bodies*; it cannot add instance fields to an existing class. RogueLibs wanted a per-instance storage slot on vanilla objects (to hang hook controllers and back-references off), so it edits the type definitions at assembly-load time. The alternative without a preloader patcher is `ConditionalWeakTable<TKey, TValue>` — that's what WizardMod's quest attribution uses, and it's usually the right choice for our mods (no extra install step, no patcher DLL).

Defensive startup in `RogueLibsCore/RogueLibsPlugin.cs Awake()`: if the patcher DLL is found in `plugins/` it auto-moves it to `patchers/` and hard-quits with a restart message (lines 29-53); then a runtime smoke test constructs a `PlayfieldObject` and assigns `__RogueLibsHooks` inside try/catch, killing the game with a clear error if the field is missing (lines 55-76, with `[MethodImpl(NoInlining|NoOptimization)]` so the JIT can't optimize the probe away).

## Hook/factory architecture (brief)

- `IHook<T>` / `HookBase<T>` (`RogueLibsCore/Hooks/`) — a custom-behavior object attached to one game instance. A `HookController<T>` list lives in the instance's injected `__RogueLibsHooks` field, reached via extension methods in `Utilities/HookExtensions.cs` (`AddHook`/`GetHook`/`GetOrAddHook`, defined per hookable type).
- `IHookFactory<T>` registries on `RogueFramework` (`RogueFramework.cs`): `ItemFactories`, `TraitFactories`, `EffectFactories`, `ObjectFactories`, plus `NameProviders`, `CustomUnlocks`, `CustomDisasters`, and captured `tk2dSpriteCollectionData` references. Factories run at the attach points listed in the treasure map (e.g. every `InvItem.SetupDetails` postfix tries every item factory).
- Hooks implementing `IDoUpdate`/`IDoFixedUpdate`/`IDoLateUpdate` are pumped by postfixes on `Updater.Update/FixedUpdate/LateUpdate` (`Patches/Patches_Misc.cs`), held via **weak references** with reentrancy-safe deferred removal, and skipped during `levelTransitioning`.
- Public API surface: `RogueLibs.CreateCustomItem<T>()`, `CreateCustomTrait`, `CreateCustomEffect`, `CreateCustomName`, `CreateCustomSprite`, `CreateCustomAudio`, each returning a fluent builder (`RogueLibs.cs`).

## THE TREASURE MAP — which game methods RogueLibs patches, per feature

This table is the distilled value of the whole framework: for each modding feature, the exact vanilla methods that must be hooked. Patch wrapper: `Utilities/RoguePatcher.cs` (naming convention `<TargetType>_<TargetMethod>`, per-patch timing, `AnyErrors()`).

### Custom items — `Patches/Patches_Items.cs`
| Game method | Kind | Purpose |
|---|---|---|
| `InvItem.SetupDetails` | Postfix | **The attach point** — dispose old hooks, run every item factory |
| `ItemFunctions.UseItem` | Prefix | Route to custom `UseItem()`; targetable items show cursor instead |
| `InvItem.CombineItems` | Prefix | Custom combine filter/behavior |
| `InvItem.TargetObject` | Prefix | Custom use-on-object behavior |
| `InvInterface.ShowTarget` / `ShowCursorText` / `HideCursorText` / `TargetAnywhere` | Postfix | Target-cursor text and target-anywhere support |
| `InvInterface.HideTarget` | Prefix | Record "recently targeted with item X" on the agent |
| `InvSlot.SetColor` / `LateUpdate` / `UpdateInvSlot` / `SortItems` / `SortUseItems`; `EquippedItemSlot.LateUpdateEquippedItemSlot` | Post/Prefix | Slot coloring, dynamic sprite swap, count text, sorting |

### Special abilities — `Patches/Patches_Abilities.cs` (abilities are items in the special slot)
| Game method | Kind | Purpose |
|---|---|---|
| `StatusEffects.GiveSpecialAbility` | Postfix | Ability granted → `OnAdded`, start recharge/indicator coroutines |
| `StatusEffects.PressedSpecialAbility` / `HeldSpecialAbility` / `ReleasedSpecialAbility` | Postfix | Press/hold/release dispatch (hold reads `playerControl.pressedSpecialAbilityTime[]`) |
| `StatusEffects.RechargeSpecialAbility2` / `SpecialAbilityInterfaceCheck2` | Prefix | Replace the recharge/targeting coroutines with custom enumerators |
| `SpecialAbilityIndicator.ShowIndicator` | Postfix | Custom indicator sprite |

### Traits & status effects — `Patches/Patches_TraitsStatusEffects.cs`
| Game method | Kind | Purpose |
|---|---|---|
| `StatusEffects.AddStatusEffect(string,bool,Agent,NetworkInstanceId,bool,int)` | **Transpiler** | Splice hook-attach + refresh right after the game sets the effect's fields |
| `StatusEffects.AddTrait(string,bool,bool)` | **Transpiler** | Same for traits; starts update coroutine for updateable traits |
| `StatusEffects.RemoveTrait` / `RemoveStatusEffect` | Prefix+Postfix | Capture instance, fire `OnRemoved` |
| `StatusEffects.GetStatusEffectTime` | Transpiler | Replace the `9999` default with the custom effect's duration |
| `StatusEffects.GetStatusEffectHate` | Postfix | Custom hate/aggro contribution |
| `StatusEffects.UpdateStatusEffect` / `UpdateTrait` | Prefix | Swap in custom ticking enumerators (with multiplayer authority checks) |

The transpilers use **IL pattern-matching** (`Utilities/TranspilerHelper.cs` — `ReplaceRegion`/`AddRegionAfter` match instruction shapes, not fixed indices), which survives minor game updates. Copy this style for any transpiler we write.

### Disasters (level feelings) — `Patches/Patches_Disasters.cs`
`Agent.CanTeleport` (Transpiler), `LevelFeelings.GetLevelFeeling` (Transpiler), `RandomSelection.RandomSelect("LevelFeelings","Scenarios")` (Prefix — mix customs into the weighted pool), `LevelFeelings.StartLevelFeelings` / `StartAfterNotification` (Postfix — start/updating), `LevelTransition.ChangeLevel` (Transpiler — finish), `LevelFeelings.CanceledAllLevelFeelings` (Prefix — `NoD_` mutator integration).

### Unlocks & menus — `Patches/Patches_Unlocks.cs`, `Patches_ScrollingMenu.cs`, `Patches_CharacterCreation.cs`
`Unlocks.LoadInitialUnlocks` (**Transpiler** — wrap every vanilla `Unlock` in a wrapper class, then append customs; this is the registration point), `Unlocks.AddUnlock` (Postfix — merge/restore semantics), `Unlocks.CanDoUnlocks` / `isBigQuestCompleted` (Prefix), `Unlocks.SaveUnlockData2` / `LoadUnlockData2` (Prefix — **full reimplementations**, see save-safety below), `MenuGUI.OpenDailyRunScreen` (Prefix+Finalizer), and the whole `ScrollingMenu.*` family (`OpenScrollingMenu`, `SetupChallenges/SetupFreeItems/SetupItemUnlocks/SetupTraitUnlocks/SetupLoadouts/...`, `SortUnlocks`, `PushedButton`, `ShowDetails`) for rendering custom entries in every unlock menu. Custom buttons link back to wrappers via `ButtonData.__RogueLibsCustom`.

### Object interactions — `Patches/Patches_Interactions.cs` + `Interactions/`
A full re-implementation of the object button system: `PlayfieldObject.Awake` / `RecycleAwake` (Postfix — hook lifecycle), `ObjectReal.DetermineButtons` / `PressedButton` / `Interact` / `InteractFar` (Prefix — route through interaction providers), `PlayfieldObject.StopInteraction` / `get_interactable` (Prefix), `InteractionHelper.*`, `Movement.HasLOSObject360` (Prefix — LOS fix for wall-embedded objects), `WorldSpaceGUI.ShowObjectButtons` (Transpiler — inject a "Done" button). ~60 per-object providers in `Interactions/VanillaInteractions/`. Note: our EightPlayers `DialogueMenu` patches the *agent* equivalents (`Agent.DetermineButtons`/`PressedButton`) — same idea, different class.

### Names, audio, misc — `Patches/Patches_Misc.cs`
`NameDB.GetName` (Prefix — provider chain; the canonical way to add display text), `NameDB.RealAwake` (Postfix — language init), `AudioHandler.SetupDics` (Prefix+Postfix — flush queued custom `AudioClip`s exactly once), `Unlocks.AddNuggets` (Prefix — remove the 99 cap), `Updater.Update/FixedUpdate/LateUpdate` (Postfix — hook pumping), `GameController.SetVersionText` / `MainGUI.Awake` (Postfix — corner version text).

### Custom agents
**Not a first-class feature on v3 `main`** — `Patches_Agents.cs` only records the last fired bullet (`Gun.spawnBullet` postfix). Custom characters were what the abandoned **v4-prototype** was adding (`Hooks/Agents/CustomAgent.cs`, plus `CustomObject` and an event-based hook dispatcher). Our WizardMod / character-creator patch set (`CharacterSelect.RealAwake`, `Unlocks.LoadInitialUnlocks`, `NameDB.GetName`, `Agent.SetupAgentStats`, `AgentHitbox.SetupBodyStrings`) is the working solution for that gap.

## tk2d sprite injection — the proven recipe (`Sprites/RogueSprite.cs`)

Entry API: `RogueLibs.CreateCustomSprite(name, SpriteScope, byte[] png, ppu=64)`. `SpriteScope` (`Sprites/SpriteScope.cs`) names the target collection: Items, Objects, Floors, Bullets, Agents, Bodies, Interface, Decals, Walls, …

1. **Texture:** `new Texture2D(...) { filterMode = Point }` + `LoadImage(pngBytes)`.
2. **Menu sprite:** `Sprite.Create` with a **Y-flipped region** — Unity's texture origin is bottom-left, tk2d's is top-left. Forget the flip and your icon is upside down.
3. **Definition:** `CreateDefinition(texture, uvRegion, scale)` (`RogueSprite.cs:378`) builds the `tk2dSpriteDefinition` by hand — UVs with a `0.001f` epsilon inset (prevents atlas bleeding), quad `positions`, `boundsData`/`untrimmedBoundsData`, `indices = {0,3,1,2,3,0}`, and a `Material` with shader **`tk2d/BlendVertexColor`** (`:397`). Scale normalization: `64f / ppu / coll.invOrthoSize / coll.halfTargetHeight`. A highlight material clone uses `tk2d/BlendAdditiveVertexColor`.
4. **Append:** `AddDefinition(collection, definition)` (`RogueSprite.cs:433`) grows `spriteDefinitions[]`, `materials[]`, `textures[]` by one (`Array.Copy`), then `coll.inst.materialIdsValid = false; InitMaterialIds(); ClearDictionary(); InitDictionary()`. Without the reinit calls, `GetSpriteIdByName` never finds the new name.
5. **Collection capture & deferral:** collections aren't all loaded at plugin start. RogueLibs postfixes `tk2dEditorSpriteDataUnloader.Register` (`Patches/Patches_Sprites.cs`) to capture each collection by name (`"Items"`, `"ObjectReals"`, `"Agents"`, `"Bodies"`, `"Interface"`, …) and flushes a `prepared[scope]` queue of sprites created before their collection existed. Copy this pattern — eager injection against a not-yet-loaded collection is a silent no-op.
6. **GameResources side:** `DefineInternal` also writes the matching Unity `Sprite` into `GameResources` (`itemDic/itemList`, `objectDic/objectList`, `floorDic`, `wallDic`) so name lookups on the UI side work. See `game-internals/sprites-audio-localization.md` for why both sides are mandatory.
7. **Material fixup battery — the hard part.** tk2d renderers cache `sharedMaterial`, which won't match an injected definition's material, so RogueLibs patches every site where the game (re)assigns the affected sprites and forces `def.material`, keyed off the injected back-reference `def.__RogueLibsCustom is RogueSprite`: `SpawnerMain.SpawnItemSprite` (full reimplementation — including Money variants and shadow offsets), `SpawnItemWeapon`, `SetLighting2`; `ObjectReal.RefreshShader` / `Start` / `SpawnShadow`; `ObjectSprite.SetObjectHighlight` / `SetAgentOff` / `SetAgentHighlight`; `InvDatabase.ThrowWeapon` / `DropItemAmount`; `Item.DestroyMe2` / `FakeStart`; `Melee.MeleeLateUpdate`; `NuggetSlot.UpdateNuggetText`. Budget for this when adding world sprites: the definition is 20% of the work, the fixups are 80%.

**Caveat:** on v3 `main`, the Hair/FacialHair/HeadPieces/Bodies/Agents scopes are only **partially wired** — the tk2d definition is created but the `GameResources` dictionary writes are commented out ("TODO: Fix commented out scopes later" in `RogueSprite.DefineInternal`). Don't assume the Bodies/Agents path works as shipped; treat it as a sketch, not a proven recipe.

Dev utility worth stealing: `RogueSprite.Dump(collection)` exports every sprite in a tk2d atlas as PNGs — invaluable for finding exact sprite names and sizes.

## Unlock save-safety design

RogueLibs treats the unlock save file as radioactive, with three layers of protection (`Patches/Patches_Unlocks.cs`):

1. **Vanilla-compatible writes.** `SaveUnlockData2` is fully re-implemented; before serialization every `Unlock` is **deep-cloned** (`CloneUnlock`, nulling `__RogueLibsCustom`) and passed through `ReverseRogueLibsEffects`, so the `GameUnlocks.dat` on disk contains only vanilla flag combinations. A save written with the mod installed stays readable by an unmodded game.
2. **Separate custom-state file.** Custom unlocks are additionally serialized as XML to `CloudData/<slot>RLUnlocks.dat`, restored by an `Unlocks.AddUnlock` postfix when a custom unlock has no match in the loaded vanilla save (also rescues nuggets that vanilla capped at 99).
3. **Atomic replace + hard abort.** Writes use the `File.Replace(Temp.dat, GameUnlocks.dat, BackupGameUnlocks.dat)` dance with overlap guards (`curSaving`/`saveOnNext`); if the unlock patches fail at startup, `PatchUnlocks` calls `Environment.Exit(1)` rather than run with a half-patched save path.

Any mod of ours that touches `Unlocks.SaveUnlockData`-adjacent state should follow the same rule: never persist mod-specific flags into the vanilla `.dat`; keep them in a sidecar file.

## Defensive patterns worth copying

- **`SpecialInt = -488755541`** (`RogueFramework.cs`) — a sentinel for "field not set by the modder," distinguishing unset from a legitimate `0`.
- **IL pattern-matching transpilers** (`Utilities/TranspilerHelper.cs`) instead of instruction-index offsets.
- **Weak-reference update hooks** with deferred removal — no leaks when the game recycles pooled objects mid-iteration.
- **`RecycleAwake` cleanup** — the game pools and recycles `PlayfieldObject`s/`InvItem`s without re-running `Awake`; RogueLibs disposes and rebuilds per-instance hooks in both `InvItem.SetupDetails` and `PlayfieldObject.RecycleAwake` postfixes. Any per-instance mod state needs the same reset or it leaks across recycled instances.
- **Multiplayer authority checks** (`serverPlayer`/`localPlayer`) inside every custom effect/trait/ability coroutine to avoid double-processing on host+client.
- **Vanilla ID tables** — `Utilities/VanillaIdentifiers/` (`VanillaItems`, `VanillaAgents`, `VanillaTraits`, `VanillaEffects`, `VanillaMutators`, `VanillaObjects`, `VanillaAbilities`, `VanillaAudio`) are complete constant lists of the game's string IDs; use them as a lookup reference instead of grepping the decompiled switches.
- **Ubiquitous try/catch with real logging** around tk2d material assignments (`RogueFramework.LogError` pretty-prints the offending instance) — tk2d state is fragile and the game's own catches are silent.

## What v4 was becoming (context only)

`v4-prototype` was a ground-up rewrite whose headline addition was **`CustomAgent` — custom characters as a first-class hook** (`Hooks/Agents/CustomAgent.cs`), plus `CustomObject` and an event-dispatcher hook system. `v4-beta` (tag `v4.0.0-rc.1`) was a large refactor of v3: self-installing patcher, names/localization overhaul, simpler patch API, embedded-resource pipeline. Both were abandoned at discontinuation (Feb 2024). If we ever want first-class custom characters beyond the WizardMod/character-creator patch set, `v4-prototype`'s `CustomAgent` is worth a read — but as inspiration, not a dependency.
