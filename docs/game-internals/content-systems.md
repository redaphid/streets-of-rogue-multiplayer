# Content Systems — Items, Status Effects, Traits, Abilities, Unlocks

**What this covers:** how the game defines and dispatches all its content — items and inventories, status effects and traits, special abilities, the Unlock registry, and challenges/mutators — plus exactly where a mod hooks to add each content type.
**When to read it:** before adding or altering any item, effect, trait, ability, character loadout, or mutator.
**Companions:** `architecture.md` (GameController, pooling, netcode authority), `agents-ai-combat.md` (how traits/effects gate AI and combat), `sprites-audio-localization.md` (icons, tk2d world sprites, NameDB text).
All paths are relative to `decompiled/` in the main checkout (gitignored — main checkout only). Line numbers are anchors, not contracts.

---

## 0. The One Big Pattern (read this first)

Nearly every content type is a **string-keyed hardcoded `switch`**. The *existence, cost, and availability* of content lives in the `Unlock` registry; the *behavior* of each thing is a `case "SomeName":` in a giant switch somewhere. The connective tissue is the string ID.

For a single item like `"Cocaine"`, the same string flows through:

| System | Where |
|---|---|
| Registration/cost | `Unlock.unlockName` in `Unlocks.LoadInitialUnlocks` (`Unlocks.cs:1265+`) |
| Stats/flags | `InvItem.SetupDetails` switch (`InvItem.cs:423`) |
| Active behavior | `ItemFunctions.UseItem` switch (`ItemFunctions.cs:16`) |
| Display text | `NameDB.GetName("Cocaine", "Item"/"Description")` (`NameDB.cs:210`) |
| UI icon | `GameResources.itemDic["Cocaine"]` (see `sprites-audio-localization.md`) |
| Spawn eligibility | `RandomItems.fillItems` leaf nodes (§1.6) |

Scale of the scattered-flag-check pattern across the codebase: `hasTrait(...)` ≈ **765** call sites, `gc.challenges.Contains(...)` ≈ **413**, `hasStatusEffect(...)` ≈ **246**, `hasSpecialAbility(...)` ≈ **50**. Adding content vanilla-style means touching several switches; adding it via Harmony means patching them.

---

## 1. Items

### 1.1 `InvItem` — the item instance (`decompiled/InvItem.cs`, 4,025 lines)

`InvItem` is a **plain serializable data class, not a MonoBehaviour**. One instance = one stack in an inventory. The physical world pickup is a separate `Item` MonoBehaviour (`Item.cs`) holding `Item.invItem`.

Key fields (`InvItem.cs:9-256`):

- Identity: `invItemName` (the string key), `invItemRealName` (localized), `spriteName`, `invItemCount`, `slotNum`, `itemNetID`.
- Ownership: `agent`, `database`, `objectReal`, `ownerID`, `startingOwner`.
- Classification: `itemType` (string: `Money, Nugget, NonItem, Junk, Readable, Food, Consumable, Tool, Combine, Wearable, WeaponMelee, WeaponProjectile, WeaponThrown`), `Categories` (`List<string>`), `weaponCode` (`weaponType` enum: `None, WeaponMelee, WeaponProjectile, WeaponThrown`).
- Behavior flags (dozens of bools): `isWeapon`, `isArmor`, `isArmorHead`, `stackable`, `goesInToolbar`, `questItem`, `stealable`, `cantDrop`, `characterExclusive`, `hasCharges`, `rechargingItem`, `canBeUsedOn*`, `isKey`.
- Tuning: `itemValue`, `initCount`, `initCountAI`, `rewardCount`, `maxAmmo`, `healthChange`, `throwDamage`, `meleeDamage`, `speedMod`/`strengthMod`/`accuracyMod`/`enduranceMod` (wearable stat mods).
- Effect linkage: `statusEffect` (string applied when used/thrown/hits), `contents` (`List<string>`, e.g. syringe payloads).

### 1.2 `SetupDetails(bool notNew)` — the item definition switch (`InvItem.cs:423`–~3659)

Preamble localizes name/description via `gc.nameDB.GetName(invItemName, "Item"/"Description")` (`:428`), then `switch (invItemName)` with **~235 cases**. Each case calls `LoadItemSprite(...)` (binds the UI icon from `gr.itemDic` — `InvItem.cs:409-421`), sets `itemType`, `Categories`, `itemValue`, `initCount`, flags, and for weapons `weaponCode` + `isWeapon`. Examples: `"Cocaine"` → `Consumable`, Categories `Drugs`/`Movement`, `statusEffect="Fast"`; `"Pistol"` → `WeaponProjectile`, `initCount=30` (ammo).

Entry point: `ItemSetup(bool notNew)` (`InvItem.cs:393`) — sets `gc/gr/rnd/database` then calls `SetupDetails`. Called everywhere an item is instantiated.

> **Pooling gotcha:** `RevertInvItem()` (`InvItem.cs:257`) resets **every field** before reuse. If you add a field to `InvItem`, reset it there or pooled items leak state between lives.

### 1.3 `OperateItem()` — activation routing (`InvItem.cs:3659`)

What happens on click/use, routed by classification: `isWeapon` → `database.EquipWeapon/UnequipWeapon`; `isArmor`/`isArmorHead` → equip paths; `Consumable`/`Tool`/`Food` → `UseItem()`; `Combine` → targeting UI (`invInterface.ShowTarget`/`CombineItem`).

`UseItem()` (`:3818`) delegates to `itemFunctions.UseItem(this, agent)`. `addStatusEffect(...)` (`:3800`) applies the item's `statusEffect` to a target; `addStatusEffectFromContents` (`:3805`) iterates `contents` (syringes/darts).

### 1.4 `ItemFunctions.UseItem` — active behavior (`decompiled/ItemFunctions.cs:16`)

Guards (ghost, `hasTrait("CantInteract")`, ownership), then `switch (item.invItemName)` with ~70 cases (`Beer`, `Cocaine`, `FirstAidKit`, `Teleporter`, `RemoteBomb`, `Necronomicon`, …). Typical food case: trait gates (`OilRestoresHealth`, `agent.electronic`), `DetermineHealthChange(item, agent)` (`:1852`) → `agent.statusEffects.ChangeHealth(n)` → `SubtractFromItemCount` → `UseItemAnim` (`:1909`).

> **Multiplayer pattern:** many cases branch on `gc.serverPlayer`; clients send `agent.objectMult.CmdClientUseItem(item.itemNetID, item.invItemName)` and the server applies the real effect. A mod that only patches the local path desyncs.

Other members: `EquipArmor/UnequipArmor` (`:1951/:1977`, applies the stat mods), `TargetObject` (`:2104`, use-item-on-object switch — keys, hacking), `CombineItems` (`:2364`). Throwing lives in `InvDatabase.ThrowItem` (`InvDatabase.cs:3485`) / `ThrowWeapon` (`:3559`).

### 1.5 `InvDatabase` — the inventory container (`decompiled/InvDatabase.cs`, 5,922 lines)

MonoBehaviour on agents and item-holding objects. Holds `InvItemList`, `equippedWeapon`, `equippedArmor`, `equippedArmorHead`, **`equippedSpecialAbility`** (`InvDatabase.cs:56`), toolbar slots.

- Add: `AddItem(...)` family (`:5586-5621`; the 21-param overload at `:5621` is the workhorse — handles stacking, quest slots, network sync). `"Nugget"` short-circuits to `gc.unlocks.AddNuggets` (nuggets are meta-currency, not items). `AddItemOrDrop` (`:5552`); `AddItemPlayerStart(name, count)` (`:1702`) for starting loadouts (count 0 = `initCount`, −1 = `rewardCount`, auto-toolbars).
- Random fill: `AddRandItem(itemNum)` (`:1304`), `AddRandWeapon` (`:1396`) — see §1.6.
- Remove/drop: `SubtractFromItemCount` (`:3014-3060`), `DropItem`/`DropItemAmount` (`:2705-2892`).
- Equip: `EquipWeapon` (`:4066`), `EquipArmor` (`:4184`), `EquipArmorHead` (`:4232`), `Unequip*` (`:4324-4511`).
- Query: `HasItem(name)` (`:5311`), `FindItem(name)`.

World pickups are spawned by `SpawnerItem.spawn(itemName)` (`decompiled/SpawnerItem.cs`) — builds a fresh `InvItem`, assigns to `Item.invItem`, `NetworkServer.Spawn` in MP.

### 1.6 Loot selection — the weighted random tree

`RandomItems.fillItems()` (called from `RandomSelection.cs:78`) builds a **hierarchical weighted deck** via `RandomSelection.CreateRandomList(name, "Items", "Item")` + `CreateRandomElement(list, childName, weight)`. Non-leaf nodes point at other lists (`"ItemAny" → "ItemTier1/2/3" → "Gun1" → "Pistol"`); leaves are real `invItemName`s.

`RandomSelection.RandomSelect(rName, rCategory)` (`RandomSelection.cs:96`) descends recursively; non-streaming mode draws **without replacement** (shuffled deck refilled from `elementListDefinition`). Consumers build list names contextually: `InvDatabase.AddRandItem` uses `agentName + "SpecialInv"`, `objectName + "SpecialInv"` (`InvDatabase.cs:1341-1351`); NPC weapons use `agentName + "Weapon" + n` (`:1420-1450`). Challenge-driven swaps apply afterward (`SwapWeaponTypes`, e.g. `NoGuns`).

Unlock-gating of items happens at the **menu/reward layer** (`ScrollingMenu.cs:2436` filters by `gc.unlocks.IsUnlocked(name, "Item")`), not inside the loot tree.

---

## 2. Status effects — `decompiled/StatusEffects.cs` (14,599 lines)

`StatusEffects` is a MonoBehaviour on each Agent owning **both** effects and traits: `List<StatusEffect> StatusEffectList` (`:22`) and `List<Trait> TraitList` (`:24`).

> **Crucial architectural fact: a status effect IS a timed trait.** `AddStatusEffect` creates the timed `StatusEffect` record AND calls `AddTrait(statusEffectName)` (`StatusEffects.cs:4929`). While `"Fast"` is active, `hasTrait("Fast")` is true. `RemoveStatusEffect` removes the trait again. Put apply/undo behavior in the trait switches; put periodic behavior in the ticker.

`StatusEffect` record (`decompiled/StatusEffect.cs`): `statusEffectName`, `curTime`, `infiniteTime`, `keepBetweenLevels`, `dontRemoveOnDeath`, `removeOnKnockout`, `causingAgent`, `effectCoroutine`.

### 2.1 `AddStatusEffect(...)` (~15 overloads, `StatusEffects.cs:4541-4618`)

The full overload `(name, showText, causingAgent, cameFromClient, dontPrevent, specificTime)`:

1. Guards: dead/ghost, `agent.preventStatusEffects` (shows `"NoEffect"`), immunities, mutual-exclusion pairs (Dizzy/DizzyB).
2. Network broadcast via `agent.objectMult.AddStatusEffect(...)`.
3. **First switch** (`:4647`): per-effect flags (`keepBetweenLevels`, `removeOnKnockout`, …) + `AddictCheck()` bookkeeping for drugs.
4. Duration: `specificTime != -1 ? specificTime : GetStatusEffectTime(name)` (another switch, `:4285`); `9999` = infinite.
5. Dedup — if already present, just refresh `curTime`. Otherwise create the record, add the display piece, start the ticker coroutine, and `AddTrait(name)`.

### 2.2 `UpdateStatusEffect` — the ticker (`StatusEffects.cs:4938`)

A ~1 s-cadence coroutine loop per active effect: decrements `curTime`, then a **per-tick switch** applies periodic behavior — `"OnFire"` → `ChangeHealth(-5)` + death attribution; `"Poisoned"` → `ChangeHealth(-2)` (−1 under the `LowHealth` challenge); `"RegenerateHealth"` → +2; `"Withdrawal"` drains above a threshold. On exit calls `RemoveStatusEffect(...)` (`:5363-5386`), which stops the coroutine and calls `RemoveTrait(name)`.

**Simple vs complex:** `"Fast"` does nothing in the ticker — its whole behavior is `AddTrait("Fast")` → `agent.FindSpeed()` recompute (undone on remove). `"OnFire"`/`"Poisoned"` have real per-tick bodies with attribution and challenge-sensitive magnitudes.

Related pickers: `AddStatusEffectSpecial` (`:8086`, syringe/dart resolution), `ChooseRandomBadStatusEffect` (`:4203`), `ChooseRandomDartStatusEffect` (`:4208`).

---

## 3. Traits

Storage: `StatusEffects.TraitList`; `Trait` record (`decompiled/Trait.cs`): `traitName`, `requiresUpdates`, `addedInGame`, `upgradedOriginalTrait`.

Query: `hasTrait(string)` (`StatusEffects.cs:7993`) — a linear scan, and the single most-used content check in the game (~765 sites gating behavior in `Agent.cs`, `AgentInteractions.cs`, `ItemFunctions.cs`, `BulletHitbox.cs`, …).

`AddTrait(name, isStarting, justRefresh)` (`StatusEffects.cs:5677`):

1. Dedup — treats `name` and `name+"2"` as the same family (trait tiers).
2. Consults the Unlock registry for tiering semantics: `gc.unlocks.GetUnlock(traitName, "Trait")` — `isUpgrade` removes the base trait, `replacing` swaps, `removal` marks a negative trait that cancels another. Upgrade chains like `MoreKnockingThroughWalls → MoreKnockingThroughWalls2` are wired here.
3. Adds the record, network-syncs via `objectMult.AddTrait`.
4. **Second switch on `traitName`** (`:5778`, ~200 cases): **most cases are empty** — those traits are passive flags read elsewhere via `hasTrait`. Cases with bodies do one-time setup: `"AlwaysCrit"` → `critChance += 100`; `"BadVision"` → `LOSRange = 1.68`; `"IncreaseAllStats"` → stat setters; relationship traits (`"Likeable"`, `"Naked"`) loop `gc.agentList` adjusting rels.

`RemoveTrait(name, onlyLocal)` (`:6845`) mirrors the switch to reverse the setup.

Trait display names come from the **StatusEffect** localization category (see §5.1) — traits and effects share naming space.

---

## 4. Special abilities

There is no ability class. An ability = **a special `InvItem` in a dedicated slot** + a per-agent string + dispatch switches.

### 4.1 Storage

- `InvDatabase.equippedSpecialAbility` (`InvDatabase.cs:56`) — the ability's `InvItem`; its `invItemCount` doubles as charge/ammo/cooldown counter.
- `Agent.specialAbility` (`Agent.cs:189`) — the string key. Also `specialAbilityAttack` (`:585`), `hasSpecialAbilityArm2` (`:621`), and `oma.superSpecialAbility` (the Big-Quest-completed upgrade, checked alongside `agentName` everywhere).
- `hasSpecialAbility(name)` (`StatusEffects.cs:8008`) compares `equippedSpecialAbility.invItemName`.

### 4.2 Assignment — `StatusEffects.GiveSpecialAbility(abilityName)` (`StatusEffects.cs:12862`)

Creates `new InvItem{invItemName = abilityName}`, runs `SetupDetails(false)` (so the ability needs a `SetupDetails` case like any item), assigns the slot and `agent.specialAbility`. Then a switch sets up per-ability plumbing: `RechargeSpecialAbility(name)` for cooldown abilities (`Possess`, `MechTransform`, `ChloroformHankie`, …), `SpecialAbilityInterfaceCheck()` for targeting abilities (`Bite`, `Enslave`, `MindControl`, …), `specialAbilityAttack = true` for attack-style (`Charge`, `Lunge`), `hasSpecialAbilityArm2` for arm-swap visuals.

### 4.3 Trigger — `StatusEffects.PressedSpecialAbility()` (`StatusEffects.cs:13564`)

`switch (agent.specialAbility)` dispatches activation: target-finding via `FindSpecialAbilityObject()` (`:13010`), LOS checks, then network via `objectMult.SpecialAbility(...)` and the ability coroutine. Some "abilities" trigger as items instead (`Depossessor`, `MechTransformItem` in `ItemFunctions.UseItem`).

### 4.4 Who gets what

`Agent.SetupAgentStats` (`Agent.cs:3711`) — the giant per-`agentName` chain: stats (`SetStrength/Endurance/Accuracy/Speed`), `GiveSpecialAbility("…")`, starting traits (`AddTrait`), starting items (`AddItemPlayerStart`). The `"Custom"` character reads `customCharacterData` (`:3916`) instead. Abilities register as `Unlock` type `"Ability"` (`Unlocks.cs:1617+`); agents recommend abilities via `unlock.recommendations` (`:1475`).

---

## 5. Unlocks — `decompiled/Unlock.cs` (238) + `Unlocks.cs` (3,972)

### 5.1 The `Unlock` record

Fields: `unlockName`, `unlockType`, `unlocked`, `nowAvailable`, three cost tiers (`cost` = shop/reward, `cost2` = loadout, `cost3` = character creation), relationship lists (`prerequisites`, `cancellations`, `recommendations`, `leadingTraits`, `leadingItems`, …), and modifiers (`removal`, `replacing`, `isUpgrade`, `cantLose`, `freeItem`, `onlyInCharacterCreation`, `dcUnlock`).

`CreateUnlock(...)` maps **unlockType → localization categories** (`Unlock.cs:117`):

| unlockType | name category | description category |
|---|---|---|
| `Item` | `Item` | `Description` |
| `Agent` | `Agent` | `Description` |
| `Trait` | **`StatusEffect`** | `Description` |
| `Ability` | `Item` | `Description` |
| `Challenge` | `Unlock` | `Unlock` |
| `BigQuest` | `Unlock` | `Description` |
| `Floor` | `Interface` | `Unlock` |
| `Extra` | (none — internal progress flags) | |

### 5.2 The registry

- Master list: `gc.sessionDataBig.unlocks`. Lookup: `GetUnlock(name, type)` (`Unlocks.cs:1237`), `IsUnlocked` (`:358`), `IsUnlockedAndActive` (`:386`).
- **`LoadInitialUnlocks()` (`Unlocks.cs:1265`–~3625) is the ~2,300-line hardcoded registry** of everything: Floors, Agents (`:1456+`), Abilities (`:1617+`), Traits (`:1705+`), Challenges (`:1308+`), Items (`:3170+`), BigQuests, Loadouts.
- **`AddUnlock(...)` (`:1173`) has save-merge semantics:** if a matching `tempUnlock` was loaded from the save, it is reused with its relationship lists cleared and re-populated — so new content and changed relations merge safely onto existing saved unlock state across versions. Consequence: set relationship lists in `LoadInitialUnlocks` (they're rebuilt each load), not at runtime.
- Award: `DoUnlock(name, type)` (`:205`) sets `unlocked`, fires notifications/achievements, saves; `DoUnlockProgress` for counted unlocks. Currency: `AddNuggets`/`SubtractNuggets`. Debug: `UnlockEverything()` (`:3950`).
- Menus enumerate the registry: `ScrollingMenu.cs:551/1680/2032/2436` filter by `unlockType`, `IsUnlocked`, `nowAvailable`, `onlyInCharacterCreation`, honoring `prerequisites`/`cancellations`.

### 5.3 Challenges / mutators

Challenges are `Unlock` type `"Challenge"`. Active ones populate `gc.challenges` (`List<string>`, `GameController.cs:573`, copied from `sessionDataBig.challenges` at `GameController.cs:1301`). Rules are altered by **~413 scattered `gc.challenges.Contains("X")` checks** — e.g. `"NoGuns"`/`"NoMelee"` (weapon swaps), `"LowHealth"` (tick damage halved in `UpdateStatusEffect`), `"SuperSpecialCharacters"` (`Agent.SetupAgentStats:3733`). Level-feeling removals use the `"NoD_*"` prefix.

---

## 6. Where a modder hooks to add content

Vanilla "adding" = adding a case to N switches. With Harmony you patch those same switch-hosting methods (postfix for registration, prefix for behavior interception). RogueLibs' exact patch map for each feature is catalogued in `../modding/roguelibs-reference.md`; WizardMod and the character-creator repo are worked examples.

### Add an ITEM
1. Registration: `Unlocks.LoadInitialUnlocks` → `new Unlock("MyItem","Item",…)` + `AddUnlock`.
2. Definition: `InvItem.SetupDetails` → a `case "MyItem":` (itemType, Categories, flags, `LoadItemSprite`, tuning).
3. If actively usable: `ItemFunctions.UseItem` case (or `TargetObject`/`CombineItems`). Equip-type items need no behavior case (flag-driven).
4. Text: `NameDB` rows for `Item` + `Description` (patch `NameDB.GetName` — the WizardMod pattern).
5. Icon + world sprite: `gr.itemDic["MyItem"]` AND a tk2d `itemSprites` definition — two separate systems; see `sprites-audio-localization.md`.
6. Spawnability: a `CreateRandomElement` leaf in the `RandomItems.fillItems` tree.
7. If you extend `InvItem` with fields: reset them in `RevertInvItem`.

### Add a STATUS EFFECT
1. `AddStatusEffect` first switch — flags case.
2. `GetStatusEffectTime` — duration case.
3. `UpdateStatusEffect` ticker — periodic behavior (if any).
4. `AddTrait`/`RemoveTrait` switches — apply/undo (AddStatusEffect auto-calls AddTrait).
5. Register as an Unlock (usually type `"Trait"`) + NameDB `StatusEffect`/`Description` rows.

### Add a TRAIT
1. `Unlocks.LoadInitialUnlocks` — `new Unlock("MyTrait","Trait",…)` (set `isUpgrade`/`replacing`/`removal` for tiers).
2. `AddTrait` switch — one-time setup (or leave empty; passive flag).
3. `RemoveTrait` — reverse it.
4. The real work: `hasTrait("MyTrait")` checks at every behavior site you want to branch.
5. NameDB rows (category `StatusEffect`); grant via `SetupAgentStats` or custom-character data.

### Add a SPECIAL ABILITY
1. `Unlocks` — type `"Ability"`.
2. `InvItem.SetupDetails` case (abilities are InvItems; `initCount` = charges).
3. `GiveSpecialAbility` switch — recharge/interface/arm setup.
4. `PressedSpecialAbility` switch — activation; `FindSpecialAbilityObject` case if targeted.
5. `RechargeSpecialAbility` wiring if timed; NameDB (`Item`/`Description`).
6. Multiplayer: extend `ObjectMult.SpecialAbility` handling (`convertSpecialAbilityToInt`).
7. Assign via `SetupAgentStats` (`GiveSpecialAbility`) or unlock `recommendations`.

### Add a CHALLENGE/MUTATOR
1. `Unlocks` — type `"Challenge"` (ScrollingMenu picks it up automatically).
2. `gc.challenges.Contains("MyChallenge")` checks at the rule sites you want to alter.
3. NameDB `Unlock` rows.

### Cross-cutting gotchas
- **Pooling:** new fields on `InvItem`/`StatusEffect`/`Agent` must be reset in the matching `Revert*`/recycle path.
- **NameDB:** every content string needs display text or `GetName` silently yields blanks (it's wrapped in try/catch).
- **Server authority:** most item/effect/ability application is gated on `gc.serverPlayer` with `objectMult.Cmd*/Rpc*` mirrors — patch both sides or desync.
- **Save-merge:** unlock relationship lists are cleared and rebuilt on every load; define them in `LoadInitialUnlocks`-time code.

### Key file map
`InvItem.cs` (definition/instance) · `InvDatabase.cs` (inventory/equip) · `ItemFunctions.cs` (active use) · `Item.cs`/`SpawnerItem.cs` (world pickups) · `RandomItems.cs` + `RandomSelection.cs` (loot tree) · `StatusEffects.cs` (effects + traits + abilities + health) · `StatusEffect.cs`/`Trait.cs` (records) · `Unlock.cs`/`Unlocks.cs` (registry) · `NameDB.cs` (text) · `Agent.cs` `SetupAgentStats` (loadouts).
