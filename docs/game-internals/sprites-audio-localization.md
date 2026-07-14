# Sprites, Audio, and Localization — the asset pipeline

**What this covers / when to read it.** How Streets of Rogue resolves art, sounds, and display text from string IDs, and exactly what a mod must inject (and where) for a new asset to actually show up. Read this before adding any custom icon, character body, world sprite, sound, or display name — and read the WizardMod post-mortem below before copying its approach. All game paths are relative to `decompiled/` (which lives only in the main checkout, not in worktrees). The proven runtime-injection implementation for the hard tk2d half is documented in [`../modding/roguelibs-lessons.md`](../modding/roguelibs-lessons.md).

## The headline: there are TWO separate sprite systems

1. **`GameResources` Unity-`Sprite` dictionaries** (`decompiled/GameResources.cs`) — read **only** by UnityEngine.UI `Image.sprite` consumers: inventory slots, the HUD ability slot, character-select portraits, quest panels, dialogue portraits, the level editor.
2. **tk2d sprite collections** (`tk2dSpriteCollectionData`) — read for **everything rendered in the game world**: floor items, objects, agent bodies. Lookups go through `tk2dBaseSprite.SetSprite(collection, name)` / `collection.GetSpriteIdByName(name)`.

Both systems are indexed by the **same string keys** but hold **physically different assets**. Populating one does not populate the other. A mod that writes only `gr.itemDic[name]` gets a working inventory icon and a blank floor item; a mod that injects only a tk2d definition gets a world sprite and a blank icon. Feed both.

## GameResources dictionaries — who reads what

`GameResources.SetupDics()` (`decompiled/GameResources.cs:104`+) fills each `Dictionary<string, …>` from a parallel, index-ordered `List` of imported assets (e.g. `itemDic.Add("AccuracyMod", itemList[0])` at `GameResources.cs:319`). It runs very early — from `GameController.Awake` (`GameController.cs:1032/1038`), before any menu — and `GameResources` is `DontDestroyOnLoad` (`GameResources.cs:85`), so entries you add survive floor reloads.

| Dictionary | Type | Read by (purpose) |
|---|---|---|
| `wallPrefabDic` | `GameObject` | `LoadLevel` wall building |
| `objectPrefabDic` | `GameObject` | Object spawning by name (`SpawnerObject.cs:70`, `LoadLevel.cs:1404`) |
| `itemDic` (`GameResources.cs:42`) | `Sprite` | **UI item icons only** → `InvItem.itemIcon` |
| `objectDic` (`:46`) | `Sprite` | Object UI icons (editor / minimap) |
| `bodyDic` (`:50`) | `Sprite` | Character-select / portrait body `Image` (`"<agentName>S"` keys) |
| `bodyGDic` (`:54`) | `Sprite` | Portrait body for Gorilla-family body types |
| `hairDic`, `facialHairDic`, `eyesDic`, `headPiecesDic`, `headDic` | `Sprite` | Portrait layer `Image`s |
| `wallDic`, `floorDic` | `Sprite` | Tile / editor UI |

**Gotcha:** none of these feed in-world rendering. They are UnityUI-only.

## Item icons (UI path) vs floor items (tk2d path)

**Inventory/HUD icon.** `InvItem.LoadItemSprite(myName)` (`decompiled/InvItem.cs:409`) does exactly one thing: `itemIcon = gr.itemDic[myName]` inside a try/catch, and records `spriteName = myName`. Consumers assign it straight to UnityUI: `InvSlot.cs:2266` (`itemImage.sprite = curItemList[slot].itemIcon`, also `:2584`, chest slots `:2313/:2364`) and `EquippedItemSlot.cs:138` (weapon/armor/**special-ability** slots). Each item's `case` in the giant `InvItem.SetupDetails` switch (`InvItem.cs:423`) calls `LoadItemSprite("SpriteName")` to bind its icon.

**Gotcha:** the try/catch means a missing key fails **silently** — `itemIcon` stays null and the slot renders blank. No log, no exception.

**In-world floor item.** The physical `Item` MonoBehaviour has a `tk2dSprite itemImageReal` (`Item.cs:61`). `Item.cs:814` calls `gc.spawnerMain.SpawnItemSprite(invItem, itemImageReal, this)`; that method (`SpawnerMain.cs:4212`) does `itemImage.SetSprite(gc.spawnerMain.itemSprites, item.spriteName)` (`:4217`) — a **tk2d collection lookup by name**, also wrapped in a silent `try{}catch{}` — and assigns `sharedMaterial = itemsMaterial` (`:4214`). Tossed items use the **`objectSprites`** collection + `objectRealsMaterial` instead (`Item.cs:809-810`). The collections are `tk2dSpriteCollectionData` fields on `SpawnerMain` (`itemSprites` at `SpawnerMain.cs:486`, `objectSprites` at `:488`).

So for one item name, `itemDic[name]` drives the icon and `itemSprites` + `GetSpriteIdByName(name)` drives the world sprite. Same key, two stores.

## Agent bodies — 8 directional tk2d sprites, tinted only in-world

- `AgentHitbox.SetupBodyStrings()` (`decompiled/AgentHitbox.cs:1323`) fills `agentBodyStrings` with `agentName + direction` for the 8 compass facings (`"N"`, `"NE"`, … `"NW"`).
- The per-frame render (`AgentHitbox.cs:2245`) does `body.SetSprite(body.GetSpriteIdByName(agentBodyStrings[index]))`, falling back to `"Generic" + dir` (`:2248`). Hair/eyes/head/headpiece resolve the same way (`:2064-2244`). Bodies are 8 **static** sprites — the game switches between them; there is no walk-cycle clip, so `tk2dSpriteAnimator` injection is not needed for bodies or items (it *is* needed for animated FX like bullets and fire).
- Unknown agent names degrade gracefully: `chooseHairType` (`AgentHitbox.cs:1693`) and `chooseSkinColor` (`:1497`) run `RandomSelection` lookups in try/catch with fallbacks (`"NormalHigh"` at `:1716`), so a custom agentName won't crash appearance setup.
- **Tinting only exists in-world.** Per-part colors (`skinColor`, `hairColor`, `legsColor`, `footwearColor`, …) are applied to the tk2d sprites by the color pass in `ObjectSprite.cs:1208-1363`. The character-select portrait is a flat `bodyDic["<agentName>S"]` UnityUI `Image` (`CharacterSelect.cs:1502`, also `:1852/:2266`; Gorilla bodies via `bodyGDic` at `:1490`) — **no tint is ever applied to it**. A character whose identity is "existing body + custom colors" looks like the plain base character on the select screen.

## tk2d internals — what runtime injection requires

- `tk2dSpriteCollectionData` (`decompiled/tk2dSpriteCollectionData.cs`) holds `tk2dSpriteDefinition[] spriteDefinitions` (`:17`). `GetSpriteIdByName(name)` (`:143`, and the `(name, defaultValue)` overload at `:148`) returns an index, `-1` if missing. `inst` is the platform-active instance actually searched.
- `tk2dBaseSprite.SetSprite(string)` resolves through `GetSpriteIdByName(name, -1)` (`tk2dBaseSprite.cs:221/:252`).
- **There is no public "add sprite" API.** Injection means, at runtime: (1) hand-build a `tk2dSpriteDefinition` (name, quad `positions`/`uvs`/`indices`, `boundsData`/`untrimmedBoundsData`, `material` with your `Texture2D`); (2) grow the fixed `spriteDefinitions[]`, `materials[]`, `textures[]` arrays (allocate bigger, `Array.Copy`); (3) invalidate and rebuild the collection's material ids and name dictionary; (4) repeat for **each** collection the sprite must appear in (agent body collection for characters, `SpawnerMain.itemSprites` for floor items, `SpawnerMain.objectSprites` for objects/toss).
- **Gotcha:** tk2d renderers cache `sharedMaterial`, which won't match an injected definition's new material — after injection you must also force the correct material at every site where the game (re)assigns the sprite, or the mesh renders with the wrong atlas texture. RogueLibs needed roughly a dozen patches for this alone; see the recipe and patch list in [`../modding/roguelibs-lessons.md`](../modding/roguelibs-lessons.md) — it is the proven implementation (`RogueSprite.CreateDefinition` / `AddDefinition` at `RogueLibsCore/Sprites/RogueSprite.cs:378/:433`).

## Recipe: inject a new sprite that works everywhere

For a new item sprite named `"MyItem"`:
1. `Texture2D tex = new Texture2D(2, 2) { filterMode = FilterMode.Point }; tex.LoadImage(pngBytes);`
2. **UI icon:** `gr.itemDic["MyItem"] = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 64f);` — do this **eagerly** (e.g. a postfix on `GameResources.SetupDics`), not lazily on first item construction, or early UI reads miss it.
3. **World sprite:** build a `tk2dSpriteDefinition` for `tex` and append it to `gc.spawnerMain.itemSprites` (per the tk2d section above / the RogueLibs recipe).
4. **Material fixup:** patch the assignment sites that touch your sprite (for items minimally `SpawnerMain.SpawnItemSprite`) so the renderer's material is your definition's material rather than the cached atlas `itemsMaterial`.
5. If the sprite is also thrown/tossed or belongs to an object, repeat 3-4 for `objectSprites`.

For a new character body: 8 tk2d definitions named `"MyAgentN"` … `"MyAgentNW"` in the agent body collection, plus `gr.bodyDic["MyAgentS"]` for the portrait — or the cheaper aliasing approach (see post-mortem) with its known portrait-tint limitation.

## WizardMod asset post-mortem

WizardMod wires assets three ways (`WizardMod/ChaosMagic.cs`, `WizardMod/WizardCharacter.cs`):
1. A postfix on `InvItem.SetupDetails` (`ChaosMagic.cs:24-34`) lazily runs `InjectSprite()` (`:36-54`): loads the embedded `ChaosMagic.png`, `Texture2D.LoadImage`, and writes `gr.itemDic["ChaosMagic"] = Sprite.Create(...)`. The embedded resource itself is fine — the csproj `<EmbeddedResource>` and `LoadEmbedded`'s `EndsWith("ChaosMagic.png")` match (`Plugin.cs:28-42`); there is **no resource-name mismatch**.
2. `AliasPortraitSprites()` (`WizardCharacter.cs:54-62`): `gr.bodyDic["WizardS"] = gr.bodyDic["VampireS"]` (and `bodyGDic`).
3. A postfix on `AgentHitbox.SetupBodyStrings` (`WizardCharacter.cs:154-165`) rewrites `"Wizard"+dir` body strings to `"Vampire"+dir`, so in-world rendering resolves against existing tk2d definitions.

What actually resolves: the HUD ability icon (after first Wizard spawn), the select-screen portrait (as a Vampire), and the in-world body (Vampire tinted purple via `legsColor`, `WizardCharacter.cs:149`). The three concrete failure modes:

- **(a) Lazy icon timing → blank icon before first spawn.** `InjectSprite` only fires when an `InvItem` named `"ChaosMagic"` runs `SetupDetails` — i.e. when a Wizard is actually spawned into a level (`StatusEffects.GiveSpecialAbility` constructs the ability item at `StatusEffects.cs:12868-12870`). Any UI that reads `gr.itemDic["ChaosMagic"]` before that (ability preview, first HUD refresh) hits a missing key, and `LoadItemSprite`'s silent try/catch leaves `itemIcon = null` → blank. **Fix:** inject eagerly at plugin startup or in a `GameResources.SetupDics` postfix.
- **(b) tk2d collections never populated → blank in-world item.** Nothing is ever added to `itemSprites`/`objectSprites`, so any code path that renders ChaosMagic as a physical world item (`SpawnerMain.SpawnItemSprite` → `GetSpriteIdByName` → `-1` → silent catch) shows nothing/stale pooled sprite. Special abilities are `cantDrop` so this is usually latent — but it fires the moment anything spawns or spills the item. **Fix:** the tk2d injection recipe above.
- **(c) Untinted Vampire portrait.** The alias copies the raw Vampire portrait `Sprite`; the Wizard's purple robe is applied only by the in-world `ObjectSprite` color pass (`ObjectSprite.cs:1309/:1319`), never to the flat portrait `Image`. On the select screen the Wizard is indistinguishable from a Vampire. **Fix:** ship a real `"WizardS"` portrait sprite (a one-off pre-tinted PNG is enough — the portrait is plain UnityUI, no tk2d work needed).

Minor: `Sprite.Create(..., 64f)` PPU only matters to UI if a slot calls `Image.SetNativeSize()` (icon may render at the wrong scale vs atlas-imported icons). No shader/material issue exists on the UI path; the in-world material path is never reached because of (b).

## Audio

- `AudioHandler.SetupDics()` (`decompiled/AudioHandler.cs:76`) fills `Dictionary<string, AudioClip> audioClipDic` (`:18`) with **399 named clips** from `audioClipRealList`. Called from `GameController.Awake` (`GameController.cs:1094/:1101`); `AudioHandler` is persistent.
- Play API: `Play(PlayfieldObject, string clipName)` (`AudioHandler.cs:1546`) → the full overload (`:1575`) handles multiplayer attribution; `PlayMust` (`:1561`) bypasses title-screen gating; actual playback via `PlayClipAt(audioClipDic[text], …)` (`:3112/:3120`) with pitch/volume/3D/looping/chaining. A `preventPlaying` list dedupes (`:1578`).
- **Adding a sound:** postfix `AudioHandler.SetupDics` and do `audioClipDic["MyClip"] = clip` (also append to `audioClipRealList` for completeness), then `gc.audioHandler.Play(pfo, "MyClip")`. Build the `AudioClip` from a WAV/OGG at plugin load (RogueLibs' `RogueUtilities.ConvertToAudioClip` shows how, including the prepared-clips queue for the case where your clip is ready before `SetupDics` runs — see `roguelibs-lessons.md`). WizardMod needed no new audio; it reuses `"CantDo"`, `"Recharge"`, `"MindControlFire"` (`ChaosMagic.cs:73/:107/:150`).

## Localization / display text

- `NameDB.GetName(string myName, string type)` (`decompiled/NameDB.cs:210`) is the single resolver. `type` ∈ `"Agent"`, `"Item"`, `"Description"`, `"Object"`, `"Unlock"`, `"Interface"`, `"StatusEffect"`, `"Dialogue"`. Language comes from `gc.sessionDataBig.gameLanguage` (`NameDB.cs:47-53`); non-English rows fall back to the English column (`:255`).
- **Gotcha:** the whole lookup is wrapped in try/catch and returns `""` on an unknown key — display names for unregistered content fail as **silent blank strings**, not errors.
- **The clean mod pattern is a prefix on `NameDB.GetName`** that returns your text for your `(name, type)` pairs and lets everything else fall through. WizardMod does exactly this (`WizardCharacter.cs:85-128`) for `("Wizard","Agent")`, `("Wizard","Description")`, `("ChaosMagic","Item")`, `("ChaosMagic","Description")`, and its Big Quest keys (`UnlockName` and `"D2_"+UnlockName` with type `"Unlock"`). RogueLibs formalized the same hook into a provider chain (`Patches_Misc.cs`, `NameDB.GetName` prefix). Don't try to edit the Google2u CSV rows.
- Object display names resolve the same way: `ObjectReal.cs:1056-1057` (`GetName(objectName, "Object")` / `"Description"`).
