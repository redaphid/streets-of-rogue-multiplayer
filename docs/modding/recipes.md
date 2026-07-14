# Recipes: adding content to Streets of Rogue

**What this covers:** task-oriented checklists for adding each kind of content —
item, status effect, trait, special ability, playable character, mutator, object,
sound — with the exact vanilla hook points to patch. **When to read it:** you have
a mod building and loading ([getting-started.md](getting-started.md)) and want to
add a concrete thing. Internals are cross-referenced, not re-explained: see
[../game-internals/content-systems.md](../game-internals/content-systems.md) for
how these systems work and
[../game-internals/architecture.md](../game-internals/architecture.md) for the
GameController/`gc` singleton, pooling, and host authority.

All decompiled references are `decompiled/<File>.cs` in the main checkout.

## The one big pattern

Vanilla content is **string-keyed hardcoded switches**. One string ID (e.g.
`"Cocaine"`, `"Fast"`, `"Doctor"`) flows through every system: the `Unlock`
registry (existence/cost), a `SetupDetails`-style definition switch (stats),
a behavior switch (`ItemFunctions.UseItem`, `StatusEffects.PressedSpecialAbility`),
`NameDB.GetName` (display text), `GameResources` dictionaries (UI sprites), and
tk2d collections (world sprites). "Adding X" always means: pick a new unique
string ID, then patch each switch that must know about it. The checklists below
enumerate exactly which ones.

Two cross-cutting rules apply to nearly every recipe:

- **Multiplayer authority.** The host (`gc.serverPlayer == true`) owns world
  mutation; clients send `Cmd*` and receive `Rpc*` via each object's
  `objectMult` (`ObjectMult.cs`). If your content spawns/damages/applies effects,
  gate the authoritative part on `gc.serverPlayer` or it will double-apply or
  desync. Adding *new* Mirror RPCs to a decompiled game is painful — piggyback on
  existing string-parameter Cmds where possible (this is what RogueLibs did).
- **Object pooling.** Agents, items, and objects are **recycled, not destroyed**.
  Unity `Awake`/`Start` do not re-run; `RecycleAwake`/`RecycleStart` and
  `RevertAllVars`/`RevertInvItem` do. Any state you attach must be reset in the
  recycle path or it leaks into the next thing that reuses the instance.

Common patch idiom for the display text used by every recipe (the
"NameDB prefix" pattern, from `WizardMod/WizardCharacter.cs`):

```csharp
[HarmonyPatch(typeof(NameDB), "GetName")]
[HarmonyPrefix]
static bool GetName_Prefix(string myName, string type, ref string __result)
{
    if (myName == "MyThing" && type == "Item")        { __result = "My Thing"; return false; }
    if (myName == "MyThing" && type == "Description") { __result = "Does a thing."; return false; }
    return true;
}
```

This beats editing the Google2u CSV tables and works for all categories
(`"Item"`, `"Agent"`, `"StatusEffect"`, `"Description"`, `"Unlock"`, `"Object"`,
`"Interface"`, `"Dialogue"`). Unknown keys make `NameDB.GetName` silently return
`""` — blank names in-game mean a missing case here.

---

## Add a new ITEM

Definition and behavior live in three switches; sprites live in two places.

1. **Register the unlock** — postfix `Unlocks.LoadInitialUnlocks`
   (`Unlocks.cs:1265`): `AddUnlock(new Unlock("MyItem", "Item", unlockedFromStart))`.
   Set `cost`/`cost2`/`cost3` for shop/loadout/character-creation pricing; push
   `.categories`. Without this the item works but never appears in menus/rewards.
2. **Define it** — postfix `InvItem.SetupDetails` (`InvItem.cs:423`): when
   `__instance.invItemName == "MyItem"`, set `itemType` (`"Consumable"`, `"Tool"`,
   `"Food"`, `"WeaponMelee"`, `"WeaponProjectile"`, `"WeaponThrown"`,
   `"Wearable"`, `"Combine"`), `Categories`, `itemValue`, `initCount`, flags
   (`goesInToolbar`, `stackable`, `isWeapon` + `weaponCode`, `isArmor` + stat
   mods…), optional `statusEffect` (applied on consume/hit), and call
   `__instance.LoadItemSprite("MyItem")`.
3. **Behavior on use** — prefix `ItemFunctions.UseItem` (`ItemFunctions.cs:16`)
   for actively-used items; return `false` to skip vanilla for your item. Weapons
   and armor need no behavior switch — the equip path is flag-driven from step 2.
   Combine/target items instead hook `ItemFunctions.TargetObject`
   (`ItemFunctions.cs:2104`) / `CombineItems` (`:2364`).
4. **Display text** — the NameDB prefix pattern above, categories `"Item"` +
   `"Description"`.
5. **UI sprite** — put a Unity `Sprite` into `GameController.gameController
   .gameResources.itemDic["MyItem"]` (embedded PNG → `Texture2D.LoadImage` →
   `Sprite.Create`). This drives inventory slots, the HUD, and the ability slot.
   Inject it **eagerly** (e.g. postfix `GameResources.SetupDics`), not lazily —
   WizardMod injected lazily from `SetupDetails` and the icon could be missing on
   first display.
6. **World sprite (tk2d)** — the UI dict does **nothing** for the item lying on
   the floor: `SpawnerMain.SpawnItemSprite` (`SpawnerMain.cs:4212`) looks the
   name up in the tk2d collection `SpawnerMain.itemSprites` and silently renders
   blank on a miss. You must append a `tk2dSpriteDefinition` to that collection —
   see [roguelibs-lessons.md](roguelibs-lessons.md) for the working recipe
   (definition geometry, `tk2d/BlendVertexColor` material, array-grow +
   `InitMaterialIds()`/`InitDictionary()`, and the renderer-material fixups).
   Skippable only if the item can never exist as a floor pickup.
7. **Loot tables (optional)** — to spawn in shops/chests/NPC inventories, add a
   leaf to the weighted random tree built by `RandomItems.fillItems`
   (`RandomSelection.CreateRandomElement(list, "MyItem", weight)` under the right
   tier/category list). Menus additionally filter by
   `gc.unlocks.IsUnlocked("MyItem", "Item")`.
8. **Pooling reset** — if you attach any custom per-item state, clear it when
   `InvItem.RevertInvItem` (`InvItem.cs:257`) runs; pooled `InvItem`s are reused.
9. **Multiplayer** — consumable effects: follow the vanilla pattern where clients
   call `agent.objectMult.CmdClientUseItem(...)` and only the server applies the
   real effect. Test with two instances (`./start.sh` twice).

## Add a new STATUS EFFECT

Effects are timed traits: `AddStatusEffect` starts a ticker **and** calls
`AddTrait(name)`; `hasTrait("MyFx")` is true while active. All in
`StatusEffects.cs` (14,599 lines).

1. **Flags** — patch the first switch in `AddStatusEffect` (`StatusEffects.cs:4618`
   body): set `keepBetweenLevels`, `removeOnKnockout`, `dontRemoveOnDeath` for
   your name (transpiler or prefix-reimplementation for your case only).
2. **Duration** — `GetStatusEffectTime` (`StatusEffects.cs:4285`): return seconds;
   `9999` = infinite.
3. **Per-tick behavior** — the ticker coroutine `UpdateStatusEffect`
   (`StatusEffects.cs:4938`) runs ~1 Hz: add your periodic work (damage via
   `statusEffects.ChangeHealth(-n)`, healing, checks). Simple flag-effects need
   nothing here.
4. **Apply/undo** — because effects ride the trait machinery, one-time setup goes
   in the `AddTrait` switch (`StatusEffects.cs:5778`) and the exact reverse in
   `RemoveTrait` (`:6845`) — e.g. recompute speed (`agent.FindSpeed()`), set an
   `agent.*` bool.
5. **Text + icon** — NameDB categories `"StatusEffect"`/`"Description"`; the buff
   display icon resolves by name like item icons.
6. **Multiplayer** — `AddStatusEffect` already broadcasts via
   `agent.objectMult.AddStatusEffect(...)`; apply your effect through the real
   `AddStatusEffect` entry point (not by hand-editing `StatusEffectList`) and you
   inherit the sync.
7. **Trigger it** from an item (`statusEffect = "MyFx"` in the item definition),
   a bullet, or code: `agent.statusEffects.AddStatusEffect("MyFx", ...)`.

## Add a new TRAIT

Traits are permanent passive flags; the "work" is the checks, not the trait.

1. **Register** — `Unlocks.LoadInitialUnlocks` postfix:
   `new Unlock("MyTrait", "Trait", ...)`. Trait tiering uses `isUpgrade` /
   `replacing` / `removal` / `upgrade` on the Unlock (this is how
   `MoreKnockingThroughWalls` → `...2` chains work — `AddTrait` consults
   `GetUnlock(name, "Trait")` at `StatusEffects.cs:5677`).
2. **One-time setup (optional)** — `AddTrait` switch case (`StatusEffects.cs:5778`)
   if the trait changes a stat/flag at grant time (e.g. `"BadVision"` sets
   `agent.LOSRange`); mirror the undo in `RemoveTrait`. Most vanilla trait cases
   are empty — the trait is *read*, not *applied*.
3. **The actual behavior** — insert `agent.statusEffects.hasTrait("MyTrait")`
   checks (Harmony patches) at the gameplay sites you want to alter. This is
   where the design effort goes; there are 765 `hasTrait` call sites in vanilla
   for reference.
4. **Text** — NameDB category is `"StatusEffect"` for trait names (traits and
   effects share the naming space) + `"Description"`.
5. **Grant it** — character loadout (`Agent.SetupAgentStats` postfix calling
   `statusEffects.AddTrait`), character-creation menu (via the Unlock), or at
   runtime.

## Add a new SPECIAL ABILITY

An ability **is an `InvItem`** stored in `inventory.equippedSpecialAbility`; its
`invItemCount` doubles as charge/cooldown counter. Worked example:
`WizardMod/ChaosMagic.cs` (modeled on vanilla MindControl).

1. **Register** — `new Unlock("MyAbility", "Ability", ...)` in
   `LoadInitialUnlocks`. Characters *recommend* abilities via
   `unlock.recommendations.Add("MyAbility")`.
2. **Item definition** — `InvItem.SetupDetails` case for `"MyAbility"` (it's an
   item: sprite via `LoadItemSprite`, `initCount` = starting charges). UI icon →
   `itemDic["MyAbility"]` (eagerly; see item recipe step 5).
3. **Grant wiring** — postfix `StatusEffects.GiveSpecialAbility`
   (`StatusEffects.cs:12862`): start recharge (`RechargeSpecialAbility`) for
   cooldown abilities, enable the target interface
   (`SpecialAbilityInterfaceCheck`) for targeted ones, set
   `agent.specialAbilityAttack` / `agent.hasSpecialAbilityArm2` if applicable.
4. **Activation** — prefix `StatusEffects.PressedSpecialAbility`
   (`StatusEffects.cs:13564`); dispatch on `agent.specialAbility == "MyAbility"`,
   return `false` to skip vanilla. Held/released variants exist
   (`HeldSpecialAbility`/`ReleasedSpecialAbility`) for charge-up abilities.
   Cooldown pattern: decrement `equippedSpecialAbility.invItemCount`, refuse when
   0 (play `"CantDo"`), recharge via coroutine (ChaosMagic's is a clean copy).
5. **Effects that spawn things** (bolts, spawns): tag projectiles with
   `bullet.cameFromWeapon = "MyAbility"` for kill attribution (Big Quests read
   it), and respect host authority — spawn through `gc.spawnerMain` on the
   server, or route via a Cmd.
6. **Multiplayer** — targeted vanilla abilities sync via
   `objectMult.SpecialAbility(name, target)`; that path has a
   string→int conversion (`convertSpecialAbilityToInt`) which does not know your
   ability. Host-only or effect-level sync (spawned bullets/status effects sync
   themselves) is the pragmatic route.
7. **Text** — abilities use NameDB category `"Item"` (+ `"Description"`).

## Add a PLAYABLE CHARACTER

**Default answer: don't hand-write it.** The character-creator mod
(`~/Projects/streets-of-rogue/character-creator`) makes characters from a
`character.json` (stats, ability from a library of effect kinds, body/color
reuse, Big Quest) with zero per-character code, and accepts drop-in
`IAbilityEffect` classes for novel powers. Use it unless you need something its
format can't express (a fundamentally new rendering/AI mechanic).

The underlying patch set, if you do hand-write (this is WizardMod, file by file —
per-patch rationale in `docs/WIZARD.md`):

1. **Roster slot** — postfix `CharacterSelect.RealAwake`: insert your name into
   `slotAgentTypes` (WizardMod displaces `GangbangerB` when the 32 built-in slots
   are full). (`WizardMod/WizardCharacter.cs`)
2. **Unlocks** — postfix `Unlocks.LoadInitialUnlocks`: an `"Agent"` unlock
   (unlocked), entry in `sessionDataBig.agentUnlocks`, plus a `"BigQuest"` unlock
   if you have one.
3. **Names** — NameDB prefix: `("MyGuy","Agent")`, `("MyGuy","Description")`,
   ability and Big Quest strings.
4. **Stats/loadout** — postfix `Agent.SetupAgentStats` (`Agent.cs:3711`) when
   `agentName == "MyGuy"`: `SetStrength/SetEndurance/SetAccuracy/SetSpeed`,
   `statusEffects.AddTrait(...)`, `inventory.AddItemPlayerStart(...)`,
   `statusEffects.GiveSpecialAbility("MyAbility")`, body tint via
   `agentHitbox.legsColor` etc.
5. **In-world body** — postfix `AgentHitbox.SetupBodyStrings`
   (`AgentHitbox.cs:1323`): alias your `agentBodyStrings` entries to an existing
   body's 8 directional tk2d sprite names (`"Vampire" + dir`). Genuinely new body
   art = 8 tk2d definitions injected into the agent body collection (hard; see
   [roguelibs-lessons.md](roguelibs-lessons.md)).
6. **Portrait** — the select screen reads `gameResources.bodyDic[agentName + "S"]`
   (`CharacterSelect.cs:1502`); alias it to the base body's entry (and `bodyGDic`
   for gorilla-family). Note the portrait Image is **untinted** — an aliased
   portrait looks like the base character; only the in-world body gets your
   colors.
7. **Big Quest (optional)** — patch a tagging site for attribution
   (`BulletHitbox.HitAftermath` + `ConditionalWeakTable` in
   `WizardMod/WizardBigQuest.cs`), count via `Quests.AddBigQuestPoints`
   (server-side), and postfix `QuestSlotBig.GetQuestInfo` to restore panel text
   the vanilla `default:` branch blanks (`WizardMod/WizardQuestPanel.cs`).
8. **Pooling/authority** — stats setup runs on recycled agents too
   (`SetupAgentStats` is the re-init path, so you're safe if you only hook it);
   quest counting must be `gc.serverPlayer`-gated.

## Add a MUTATOR / CHALLENGE

1. **Register** — `new Unlock("MyChallenge", "Challenge", ...)` in
   `LoadInitialUnlocks`; the mutators menu (`ScrollingMenu`) enumerates challenge
   unlocks automatically, including mutual exclusions via `.cancellations`.
2. **Behavior** — at run start active mutators populate `gc.challenges`
   (`List<string>`); patch your rule sites and check
   `gc.challenges.Contains("MyChallenge")` (vanilla has 413 such checks — grep
   them for placement ideas).
3. **Text** — NameDB category `"Unlock"` for both name and description.
4. Level-feeling-removal mutators use the `"NoD_"` name prefix convention.

## Add an OBJECT (world furniture/machine)

1. **Prefab + registration** — build a `GameObject` with an `ObjectReal`
   subclass, a `tk2dSprite` child ("Sprite"/highlight), collider; postfix
   `GameResources.SetupDics` to add `objectPrefabDic["MyObject"] = prefab` (and
   `objectDic`/`objectList` for the UI sprite). Spawning by name then works
   through `gc.spawnerMain`/`PoolsScene.SpawnObjectReal`.
2. **Behavior** — override the `ObjectReal` contract (`ObjectReal.cs`):
   `Interact`/`InteractFar` (walk-up), `DetermineButtons` + `PressedButton`
   (menu), `UseItemOnObject` (item targeting), `DamagedObject`,
   `DestroyMeGeneric` (destruction/wreckage), `ObjectAction`. `Safe.cs` (307
   lines) is the cleanest template to copy.
3. **Text** — NameDB categories `"Object"` + `"Description"`
   (`ObjectReal.cs:1056` resolves them).
4. **World sprite** — the tk2d definition must exist in the objects collection
   (`SpawnerMain.objectSprites`); same injection problem as items.
5. **Pooling** — implement `RecycleAwake`/`RevertAllVars` resets; objects are
   pooled aggressively (`PoolsScene.cs`).
6. **Placement in generated levels** means chunk data — hard. Runtime spawning
   (via code or the EightPlayers `spawnobject` verb) is the practical route.
7. **Multiplayer** — object state changes go through `ObjectMult` Cmds/Rpcs;
   host-authoritative.

## Add a SOUND

1. Postfix `AudioHandler.SetupDics` (`AudioHandler.cs:76`): add your `AudioClip`
   to `audioClipDic["MySound"]` (and `audioClipRealList`). Build the clip from an
   embedded file (WAV/OGG → `AudioClip` conversion; RogueLibs'
   `RogueUtilities.ConvertToAudioClip` shows the working code).
2. Play with `gc.audioHandler.Play(playfieldObject, "MySound")` — 3D positioning,
   multiplayer forwarding, and dedup (`preventPlaying`) come free.
3. Vanilla has 399 named clips — reusing one (`"CantDo"`, `"Recharge"`, ...) needs
   no injection at all; grep `AudioHandler.SetupDics` for the catalog.

---

## Which approach when

- **Harmony patches on the vanilla switches** (this doc): permanent content that
  should exist in menus, saves, and multiplayer — new items, traits, abilities,
  characters, mutators. Cost: a patch per switch, pooling/authority care.
- **EightPlayers command channel / Lua BehaviorEngine**: runtime-only behavior —
  orchestrating a live session, NPC scripting, spawning test scenarios, GM-style
  events. Nothing persists and nothing appears in menus, but there's no build
  step and it works over HTTP. See
  [../eightplayers/command-channel.md](../eightplayers/command-channel.md).
- **character-creator**: playable characters, full stop. Data-driven, kid-proof,
  installer included; custom powers via drop-in `IAbilityEffect` classes.
- **RogueLibs techniques** ([roguelibs-lessons.md](roguelibs-lessons.md)): when a
  recipe above hits its hard step — real tk2d sprite injection, attaching
  per-instance data to game objects (the Cecil field-injection trick),
  save-file-safe custom unlocks, custom disasters. Don't depend on the library
  (discontinued, and its patcher DLL complicates installs); port the specific
  technique.
