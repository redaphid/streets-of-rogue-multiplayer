# Agents, AI & Combat

**What this covers:** the `Agent` god-class and its satellite components, the Brain/Goal AI system and its arbitration loop, relationships/aggro, the single damage funnel (melee, guns, explosions, fire), movement/knockback, and agent stats.
**When to read it:** before any mod that spawns, controls, damages, befriends, or modifies NPCs or players — or hooks combat.
**Companions:** `architecture.md` (GameController, spawning, netcode authority), `content-systems.md` (items/effects/traits/abilities), `sprites-audio-localization.md` (body sprites).
All paths are relative to `decompiled/` in the main checkout (the folder is gitignored — not present in worktrees). Line numbers are anchors into the current decompile, not contracts.

---

## 1. Agent.cs — the NPC/player god-class

`Agent : PlayfieldObject` (`decompiled/Agent.cs`, ~363 KB). Every character — player or NPC — is an Agent. Behavior is split across sibling MonoBehaviours that the Agent caches as fields.

### 1.1 Component references (fields near `Agent.cs:13-71`)

| Field | Type | Owns |
|---|---|---|
| `agentInteractions` | `AgentInteractions` | interact-button menus & their actions |
| `statusEffects` | `StatusEffects` | health changes, effects, traits, abilities, death |
| `relationships` | `Relationships` | who likes/hates whom |
| `inventory` | `InvDatabase` | items, equipped weapon/armor/ability |
| `movement` | `Movement` | teleport, knockback, LOS helpers |
| `brain` / `brainUpdate` | `Brain` / `BrainUpdate` | goal stack / the AI tick |
| `combat` | `Combat` | combat-engage AI (strafe, retreat, shoot) |
| `pathfindingAI` | `PathfindingAI` | pathing |
| `gun` / `melee` | `Gun` / `Melee` | attacks |
| `agentHitbox` | `AgentHitbox` | body sprites, hitboxes, agent-touch damage |
| `skillPoints` | `SkillPoints` | XP/level |
| `oma` | `ObjectMultAgent` | network sync **and** an AI state flag-bag (see 1.4) |

`oma` is declared on the base class (`PlayfieldObject.cs:61`), assigned at `PlayfieldObject.cs:1410`.

### 1.2 Identity fields

- `agentName` (string) — the class identity, used in nearly every string-switch in the game.
- `agentRealName` (`Agent.cs:135`) — the localized display name from `gc.nameDB.GetName(...)`; `agentOriginalName` (`:93`).
- `agentID` (int, `:131`) — also used to **index `relationships.RelList2` directly** (gotcha, §3.2).
- **`isPlayer` is an int, not a bool** (`:141`): 0 = NPC, 1–4 = player slot. Pair with `localPlayer` (bool, `:143`) to know if the player is on *this* machine in multiplayer.
- `gang` (int, `:1021`), `gender`, `firstName`/`lastName` (`:164-168`).

### 1.3 The agentName roster

67 canonical classes (enumerated in `LoadDialogue()`, `Agent.cs:7263+`):

`Actor, Alien, Artist, Athlete, Bartender, Bouncer, Businessman, Cannibal, Caveman, Celebrity, Cop, Cop2, Doctor, DrugDealer, Farmer, Firefighter, Gangbanger, GangbangerB, Ghost, Gorilla, Guard, Hacker, Hobo, Janitor, Judge, Lawyer, Magician, Mortician, Musician, OfficeDrone, Pastor, Pimp, Politician, Prostitute, Reporter, Robot, Scientist, Shopkeeper, Slave, Slavemaster, Soldier, Thief, Vampire, Worker, Zombie, Wrestler, ShapeShifter, Assassin, Comedian, Werewolf, WerewolfB, ResistanceLeader, UpperCruster, Mafia, Clerk, ButlerBot, CopBot, Cultist, Mayor, Guard2, Generic, RobotPlayer, Courier, MechPilot, MechFilled, MechEmpty, Demolitionist`

Plus sentinels: `"Custom"` (player-created character — skips name generation, uses `agentRealName`) and `"Hologram"`.

> **Gotcha — agentName mutates at setup.** `SetupAgentStats` (`Agent.cs:3733-3747`): if `oma.superSpecialAbility`, the name is rewritten in place — `Cop→Cop2`, `Guard→Guard2`, `Hobo→UpperCruster`. A check against `"Cop"` misses elite cops.

### 1.4 The `oma` flag-bag gotcha

`oma` (`ObjectMultAgent`) is nominally the Mirror sync component but doubles as AI/state storage. Flags read throughout the AI: `oma.hidden`, `oma.mindControlled`, `oma.superSpecialAbility`, `oma.bodyGuarded`, `oma.mustBeGuilty`, `oma.hasAttacked`, `oma.combatCanSee`, `oma.didAsk`, `oma.shookDown`, `oma.notReadyToEnterLevel`, `oma.finishedLevel`. When modding AI, check/set state on `oma`, not only on `Agent`.

### 1.5 Lifecycle & pooling

Agents are **pooled and recycled**, not freshly instantiated (see `architecture.md` §4). Key methods:

- `RecycleStart()` (`Agent.cs:3529`) plus paired `RevertAllVars()` on Agent, `Brain.cs:14`, `BrainUpdate.cs:131`, `Relationships.cs:59`, `AgentHitbox.cs:544`, `Combat.cs:96`. **Any custom per-agent state must be reset in this path or it leaks into the next agent recycled from the pool.**
- `SetupAgentStats(string transformationType)` (`Agent.cs:3711`) — seeds RNG (`Random.InitState(randomSeedNum + curLevelEndless + agentID)`), resolves super-special renames, creates the inventory, assigns stats/traits/ability per `agentName` (giant `else if` chain; the character loadout registry — see `content-systems.md` §4.3).
- Brain startup: `BrainUpdate.StartBrain(float AIOffset)` (`BrainUpdate.cs:192`); agents are bucketed into 6 `AIOffsetGroups` for load balancing.
- There is **no `Die()` method** — death is a state (`dead`, `justDied`) set by `StatusEffects.SetupDeath` (§4.4).

---

## 2. AI — Brain, Goals, BrainUpdate

### 2.1 Brain and Goals

`Brain` (`decompiled/Brain.cs`) is a thin container: `List<Goal> Goals` (`:10`), `bool active` (`:12`). `Start()` seeds one `InitialGoal`. Goals form a **parent/subgoal tree** (`AddSubgoal`, `ProcessSubgoals`, `HasGoalDeep`); `SubGoals[0]` is the active leaf.

> **Gotcha:** `Brain.cs` also contains a parallel `*2` API (`ProcessSubgoals2`, `AddSubgoal2`, …) that is dead/stubbed (`TerminateSubgoal2` is empty). Use the primary `Goals`/`SubGoals` tree only.

`Goal` (`decompiled/Goal.cs`): `goalName` (string), `goalCode` (`goalType`), `goalStateCode` (`goalStatus`: Inactive/Active/Completed/Failed), virtual `Activate()`/`Process()`/`Terminate()`.

`goalType` (`decompiled/goalType.cs`, 39 values): `None, InitialGoal, Battle, Flee, FleeDanger, Tattle, Follow, Interact, WalkToExit, DoJob, NoiseReact, WaitForDangerEnd, Patrol, CuriousObject, Investigate, Wander, Sleep, DoNothing, RobotFollow, Idle, Cinematic, Guard, WanderInOwnedProperty, WanderFar, Dance, Joke, ListenToJokeNPC, Sit, Steal, IceSkate, Swim, Hide, FindFire, RobotClean, Bite, Cannibalize, GoGet, GetElectionResults, CommitArson`. Each has a concrete class file (`GoalBattle.cs`, `GoalFlee.cs`, `GoalSteal.cs`, …).

Default-goal plumbing on Agent: `defaultGoal` (string) / `defaultGoalCode` (`Agent.cs:1267-1269`), set via `agent.SetDefaultGoal("...")`, with target setters `SetStealingFromAgent`, `SetBitingTarget`, `SetCannibalizingTarget`, `SetGoGettingTarget`.

### 2.2 BrainUpdate.MyUpdate — the AI tick

`BrainUpdate.MyUpdate()` (`BrainUpdate.cs:241`) is the per-agent AI heartbeat, but it is **not called every frame**: `LoadLevel.UpdateAIOffsetGroups()` (`LoadLevel.cs:13460`) cycles agents through 6 offset groups (~one group per 16.7 ms), calling `brainUpdate.MyUpdate()`, `combat.CombatCheck()`, and `pathfindingAI.UpdateTargetPosition()`.

What MyUpdate does:

- Caches position (`curPosition/curPosX/curPosY`, `overHole/overWall/overDanger`), with a `notMovedSinceLastAIUpdate` fast path.
- **Distance culling:** agents far from every player (>16 units; `InFastAIBounds` is a 13×10 box, `BrainUpdate.cs:1201`) get `SetBrainActive(false)` and colliders disabled (`TurnOffCollision`); `slowAIWait` throttles distant agents to only `Goals[0].Process()`. *This is why mods "don't run" on off-screen NPCs.*
- When active and `gc.loadComplete`: calls **`GoalArbitrate()`** then `brain.Goals[0].Process()` (`:707-708`).
- **Per-class opportunistic behaviors** in the `losCheckAtIntervals` block (`:599-704`), a hardcoded switch on `agentName`: `"Hobo"` spots ground money → `SetDefaultGoal("GoGet")`; `"Cannibal"` scans `gc.deadAgentList` → `Cannibalize`; `"Thief"` picks a nearby target → `Steal` (sets `oma.mustBeGuilty`); `"Vampire"` → `Bite` (sets `oma.hasAttacked`).
- Also: hidden-agent pop-out (`:755-798`), slave-helmet leash explosions (distance > 17 → −200 health, `:804-877`), mind-control distance break (`:800`), noise emission while loud and moving (`:741`).

### 2.3 GoalArbitrate — desirability scoring (`BrainUpdate.cs:1981`–~3550)

The decision core. Pattern:

1. Resets a pool of pre-allocated temp goals (`tempGoalBattle`, `tempGoalFlee`, `tempGoalSteal`, … declared `:73-116` — pre-allocated to avoid GC).
2. Loops `agent.relationships.hateList` (`:2046`): for each Hostile agent with a known `lastSawPosition` (or that just hit us), scores `AssessBattle(...)` vs `AssessFlee(...)` (`Relationships.cs:4046/4056`) using team sizes, distance, and `relHate`. `mustFlee`/`wontFlee`/`zombified`/`enraged` override.
3. The highest `curDesirability` wins and becomes the active goal.
4. Falls through blocks of `if (defaultGoalCode == goalType.X && highestDesirability < N)` (`:2632-3520`) that revert to the default goal when nothing urgent scores. Thresholds are literal floats: `< 10f` drops Steal/Bite/Cannibalize/GoGet, `< 5f` for Guard/Patrol, `< 2f` for Wander, `< 1.1f` for idle poses.

### 2.4 Jobs

`jobType` (`decompiled/jobType.cs`): `None, Follow, GoHere, Ruckus, Attack, LockpickDoor, HackSomething, UseToilet, GetSupplies, GetDrugs, GetDrink, UseATM`. Stored on Agent as `job`/`jobCode` with `assignedAgent`/`assignedObject`/`assignedPos`, `employer`. Dispatched to followers from `AgentInteractions` (`FollowMe`, `GoHere`, `CauseRuckus`, `Attack`, …) and consumed by `GoalDoJob`. `Relationships.CancelJob` (`Relationships.cs:3010`) clears a job when the boss turns hostile.

---

## 3. Relationships & aggro

### 3.1 relStatus and the "Hateful" string gotcha

`relStatus` (`decompiled/relStatus.cs`): `Neutral, Aligned, Loyal, Friendly, Annoyed, Hostile, Submissive`.

> **Gotcha — the string API uses `"Hateful"`, not `"Hostile"`.** `Relationship.SetRelType(string)` (`Relationship.cs:223`, cases at `:322/:400`) maps `"Hateful"→relStatus.Hostile`. There is no `"Hostile"` string; passing it does nothing.

### 3.2 The three relationship classes

- `AgentRels.cs` — a tiny serializable DTO (7 `List<int>`s) used only for save/streaming.
- `Relationship.cs` — one pairwise record: `relTypeCode`, `initialRelTypeCode`, `relHate` (float hate meter), `relStrikes`, `lastSawPosition`/`hasLOS`, `hitNumberOfTimes`, `damageDone`, `secretHate`, `annoyedCountdown` (10 s).
- `Relationships.cs` (~5,648 lines) — the per-agent manager. `RelList` plus **`RelList2` indexed directly by `agentID`** (`SetRelHate`/`AddRelHate` at `:1782/:1812` do `RelList2[otherAgent.agentID]` inside try/catch — bad IDs fail *silently*). Convenience lists: `hateList`, `alignedList`, `loyalList`.

### 3.3 Reading and changing relationships

- Read: `GetRel(Agent)→string` (`Relationships.cs:1681`), `GetRelCode` (`:1719`), `GetRelationship` (`:1753`), `GetRelHate` (`:1933`).
- Write: `SetRel(Agent, string[, cameFromServer])` (`:1496/1501`) — **server-only unless `cameFromServer` is passed** (`if (!(gc.serverPlayer || cameFromServer)) return;`). Also `SetRelInitial` (`:1586`), `SetAllRel` (`:1638`), `SetSecretHate` (`:1604`).
- Hate meter: `SetRelHate` (`:1773`), `AddRelHate` (`:1803`). Loyalty divides incoming hate (÷3–4, `:1826-1835`); players don't accrue hate from non-hostile NPCs.
- Strikes: `SetStrikes`/`AddStrikes` (`:1878/:1900`).

### 3.4 DetermineRel — the hostility thresholds (`Relationships.cs:2871`)

Translates `relHate` + `relStrikes` into a status:

- **`relHate >= 5f` ⇒ `SetRel("Hateful")`** (verified `:2960/:2974`). This is THE hostility threshold.
- `relHate < 0` ⇒ Friendly; `relHate > 0` ⇒ Annoyed.
- Strikes: strike 1 → `SayDialogue("Strike1")`; strike 2 → Annoyed; strike ≥ 3 → `relHate += 5` (auto-hostile). `annoyedCountdown` (10 s) cools down.

### 3.5 Initial relationships — `SetupRelationshipOriginal` (`Relationships.cs:1966`–~2770)

A ~800-line string-switch on `agentName` pairs, enforcer status, `gang`, traits, and quests. Examples: two players → mutual `"Aligned"` (`:2091`); cop-family (`Cop/Cop2/CopBot/Mayor`, enforcer-`Custom`) mutually Aligned/Friendly (`:2088-2194`); Shopkeepers/OfficeDrones give strikes to `Unlikeable`/`Naked` visitors (`:2195-2204`). Trait-driven variants: `HonorAmongThieves` stops thieves stealing from you; `Mugger` changes interactions.

### 3.6 What triggers aggro

- **Being hit** (`StatusEffects.ChangeHealth`, `StatusEffects.cs:298-430`): if `damageDone >= 10` AND (`hitNumberOfTimes > 4` OR `hitNumberOfTimesInCombat > 12`) — or quest/mind-control conditions — then `SetRel(attacker, "Hateful") + SetRelHate(attacker, 5)` (`:382-385`). Nearby allies react via `HurtFriendCheck`/`HurtFriendCheck2` (`:3967/:4017`). Aligned/Loyal targets take reduced or zero damage (`:336`, `:75-82`).
- **Other paths:** `OwnCheck`/`OwnCheckPosition` (`:4601/:4503`, property violations → strikes), `EnforcerAlert`/`EnforcerCheck` (`:5182/:5157`, witnessed crimes → cops), `FollowerAlert` (`:5226`), `ProtectOwned` (`:4205`), `DeadBodyCheck` (`:3915`, seeing corpses). `LastSaw(Agent)` (`:3458`) maintains `lastSawPosition`/LOS.
- Attribution fields: `justHitByAgent`, `justHitByAgent2` (attributed), `lastHitByAgent`, plus `deathKiller`/`deathMethod` for kill credit.
- Party/control: `JoinParty`/`LeaveParty` (`:3227/:3334`), `MindControl`/`StopMindControl` (`:5309/:5444`), `StopFollowing` (`:5084`).

---

## 4. Combat & the damage funnel

**All damage flows through one funnel.** Hook here, not at individual weapons:

```
collider (MeleeHitbox/BulletHitbox/AgentHitbox .HitObject/.HitAgent)
  → victim.Damage(damagerObject, fromClient)          Agent.cs:7253
    → FindDamage(damagerObject, …)                    PlayfieldObject.cs:1600  (computes amount, sets deathMethod/deathKiller)
      → statusEffects.ChangeHealth(-n, damagerObject) StatusEffects.cs:257     (guards, aggro, applies, detects death)
        → SetupDeath(...)                             StatusEffects.cs:2110
```

### 4.1 `Agent.Damage` (`Agent.cs:7253`)

Calls `FindDamage`; if the result isn't the sentinel **`9999` (= blocked / no damage)** and we're not in HomeBase and `fromClient` is false, applies `statusEffects.ChangeHealth(-num, damagerObject)`. `fromClient` means a multiplayer client already resolved it.

### 4.2 `PlayfieldObject.FindDamage` (`PlayfieldObject.cs:1600`)

One method computes damage for ALL sources by switching on the damager's type, and sets `deathMethod`/`deathMethodItem`/`deathKiller`:

- **Agent touch** (`:1627`): Giant = 30, ElectroTouch = 15 (×1.5–×3 in water), Charge = 10 (20 if super-special/`ChargeMorePowerful`), shrunk-stomp = 200 (`deathMethod="Stomping"`).
- **Bullet** (`:1691`): `bullet.damage` scaled by shooter accuracy — `num *= 0.6f + (accuracyStatMod + moreAccuracy)/5f` (`:1723-1725`) — and sprite scale. `deathMethod = bullet.cameFromWeapon`.
- **Melee** (`:1769`): weapon (or fist) `meleeDamage * (1 + strengthStatMod/3f) * spriteScale.x` (`:1776-1779`). `deathMethod = invItemName`.
- **Explosion** (`:1799`): `explosion.damage`, LOS/range-gated attribution. **Fire** (`:1841`): `fire.damage`. **Object hazard** (`:1875`): `hazardDamage` or 30. **Item** (`:1913`): `otherDamage`.

### 4.3 `StatusEffects.ChangeHealth` (`StatusEffects.cs:245/251/257`)

The full overload does: early-out guards (teleporting/ghost/finishedLevel/hologram/mechEmpty), Aligned/Loyal damage reduction, controller vibration, the aggro escalation of §3.6, then `health += healthNum` clamped to `healthMax`, and death detection — `if (agent.health <= 0f …) SetupDeath(...)` (`:797`, `:1086`, `:1465`, `:1533`), with knockout / fell-in-hole / burnt special paths (`:1479`).

> **Never write `agent.health` directly** (outside reset paths) — you skip guards, aggro, network sync, and death detection.

### 4.4 Death — `StatusEffects.SetupDeath` (`StatusEffects.cs:2110`)

Sets `dead`/`justDied`, stops sounds/interactions (`StopInteractionsDead` `:2971`), frees slaves (`:3017`), fires quest "Dead" notifications, gore threshold (`health <= -20` → `bloodyMessed`), `zombieWhenDead` resurrection. Networked via `agent.objectMult.SetupDeath(...)`. Non-lethal: `Arrest()` (`:8826`); knockout is a health band (0 to about −10), captured via `healthBeforeKnockout`.

### 4.5 Melee chain (`decompiled/Melee.cs`, ~1,594 lines)

`Attack(bool specialAbility)` (`:409`) picks `equippedWeapon`/`fist`, `ShowMelee` (`:1146`) spawns the swing colliders. Hierarchy: `MeleeContainer` → `MeleeColliders` → `MeleeColliderBox.OnTriggerEnter2D` (`MeleeColliderBox.cs:22`) → `MeleeHitbox.HitObject` which applies `agent3.Damage(myMelee, fromClient)` (`MeleeHitbox.cs:1110`) plus `KnockBackBullet(...)` (`:1182`) and sets `justHitByAgent2` (`:514/:1119`). Cooldown: `SetWeaponCooldown` (`Melee.cs:273`). Throw: `Throw()` (`:290`).

### 4.6 Gun/bullet chain (`decompiled/Gun.cs` ~2,018, `Bullet.cs` ~1,842 lines)

`Gun.Shoot(specialAbility, silenced, rubber[, …])` (`Gun.cs:817-827`) → `spawnBullet(bulletStatus, InvItem, netID, …)` (`:214/:219`). Bullet fields: `agent` (shooter), `damage` (`Bullet.cs:22`), `bulletType` (`bulletStatus` enum: `Normal, Shotgun, Revolver, Laser, Fire, Fireball, Water, Water2, GhostBlaster, LeafBlower, ResearchGun, FireExtinguisher, None`), `cameFromWeapon` (string, `:34` — used for death attribution and quest credit). Hit: `BulletHitbox.HitObject` applies `agent.Damage(myBullet, fromClient)` (`BulletHitbox.cs:198/:259/:621/:654`) with knockback strength by bullet type (7 normal; 30/150/200 heavy).

### 4.7 Combat AI (`decompiled/Combat.cs`, ~1,787 lines)

`CombatCheck()` (`:348`) → `CombatEngage()` (`:363`, when `agent.inCombat`) or `FleeCombatEngage()` (`:1231`), driven by `GoalBattle`/`GoalFlee`. `CombatEngage` implements advance/retreat/strafe (`advanceRetreat`, `strafe`, `inMeleeRange`), corner-peeking via `CornerCombatHelper` objects (`:448`), LOS via `oma.combatCanSee`, pathing to `lastSawPosition` when the target is unseen, `ShootFar` (`:1347`) for ranged. Opponent tracking on Agent: `opponent`, `hasOpponent`, `SetOpponent`.

### 4.8 Movement, knockback, teleport (`decompiled/Movement.cs`)

- `Teleport(x,y | Vector2 | Vector3)` (`:146/:151/:157`) → `MultiplayerTeleport()` (`:164`): server uses `networkRigidbody.ServerTeleport`, local client sends `objectMult.CmdMovementTeleport`.
- Knockback family: `KnockBack` (`:905`), `KnockBackBullet` (`:1072`), `KnockBackCharge` (`:966`), `KnockBackRocket` (`:987`), `KnockBackBump` (`:1030`), `KnockBackSpecificAngleFromPoint` (`:1205`), `KnockbackSpecificVelocity` (`:1174`). They set `knockBackFrom`/`knockedByObject` and network-sync. Server skips knockback if `agent.dead && objectMult.clientHasControl` (`:1131`).
- LOS helpers: `HasLOSAgent`, `HasLOSAgent360`, `HasLOSObject360`, `FindAimTarget`, `RotateToObject`, `PathStop`.
- Agent-touch damage and hitbox swapping live in `AgentHitbox.HitAgent` (`AgentHitbox.cs:3483`) and `ChangeHitBox(string)` (`:1988`); `AgentColliderBox.OnTriggerEnter2D` (`AgentColliderBox.cs:21`) forwards to it.

---

## 5. Stats

Each stat is an int level with a `*StatMod` (current) and `*StatDefault` (starting) pair on Agent; each setter has a `doOnline` overload that syncs via `objectMult`.

- `SetStrength(int)` (`Agent.cs:6571`) → feeds melee ×`(1 + str/3)`.
- `SetAccuracy(int)` (`:6676`) → feeds bullets ×`(0.6 + acc/5)`.
- `SetEndurance(int)` (`:6585`) → **`healthMax = 80 + enduranceStatMod * 20`** (`:6599`; endurance 90 ⇒ 3000, `:6595`). **NPCs then get `healthMax = Round(healthMax / 3)`** (`:6603-6606`) — a default NPC has roughly one third of a player's health for the same stat. Challenges (`HalfHealth`, `LowHealth` ÷4, `EveryoneHatesYou`) scale further.
- `SetSpeed(int)` (`:6690`) → `FindSpeed()` (`:6716`): **`speed = speedStatMod == 90 ? 500 : 1750 + speedStatMod * 250`**, then ×0.666 if `gc.scaledDown`, ×1.5 for `Fast` (×1.2 with RollerSkates), ÷1.5 for `Slow`, ÷1.25 for `Withdrawal`, a low-health bonus for `FastWhenHealthLow[2]`, and 0 when `Paralyzed`.
- Health: `health` (float, default 100, `:89`), `healthMax` (`:169`) — change only via `statusEffects.ChangeHealth`.
- Perception/skill: `LOSRange` (`:191`), `LOSCone` (`:193`), `hearingRange` (`:195`), `modMeleeSkill`/`modGunSkill` (`:197/:199`), `modVigilant`, `modToughness`.
- Effects/traits gate everything: `statusEffects.hasStatusEffect("X")` (timed) and `hasTrait("X")` (permanent) — see `content-systems.md`.

---

## 6. Interactions ("walk up and press interact")

Dispatch chain:

1. `PlayfieldObject.Interact(Agent)` (`PlayfieldObject.cs:1500`) sets `interactingAgent` (`InteractFar` `:1533`).
2. `Agent.Interact(otherAgent)` (`Agent.cs:7065`) — annoyed NPCs refuse (`SayDialogue("AnnoyedWontTalk")`), then `ShowObjectButtons()` (`PlayfieldObject.cs:2723`).
3. `Agent.DetermineButtons()` (`Agent.cs:7094`) → `AgentInteractions.DetermineButtons(...)` (`AgentInteractions.cs:26`) — builds the context-menu buttons per agentName/quest/relationship/traits via `AddButton(name[, cost])` (`:1983-1998`).
4. Selection → `Agent.PressedButton(text, price)` (`:7105`) → `AgentInteractions.PressedButton(...)` (`AgentInteractions.cs:2008`) — a giant `switch(buttonText)` (`:2015`) with ~241 cases: follower orders (`FollowMe` `:3627`, `GoHere` `:3671`, `Attack` `:3757`), banking, bribes (`Bribe` `:6677`, `BribeCops` `:4776`), mugging (`MugMoney` `:4510`), quest hand-ins.
5. Item-on-agent uses go through `Agent.UseItemOnObject` (`:7119`).

NPCs interact autonomously via `GoalInteract.cs`.

---

## 7. Gotcha checklist (memorize these)

1. **Server authority everywhere.** `SetRel`, `AddRelHate`, damage, spawning — most mutations no-op unless `gc.serverPlayer` (or a `cameFromServer`/`fromClient` flag). Route client-side actions through `objectMult.Cmd*`.
2. **`"Hateful"` not `"Hostile"`** in the string relationship API; the enum is `relStatus.Hostile`.
3. **`relHate >= 5` = hostile**; 3 strikes ⇒ +5 hate.
4. **State lives on `oma`** (hidden, mindControlled, superSpecialAbility, combatCanSee, mustBeGuilty) — not always on `Agent`.
5. **`isPlayer` is an int** (0 = NPC, else slot number); pair with `localPlayer`.
6. **agentName mutates** at setup (Cop→Cop2, Guard→Guard2, Hobo→UpperCruster).
7. **Pooling:** reset custom fields in `RecycleStart`/`RevertAllVars` or they leak across recycled agents.
8. **AI is distance-culled** (>16 units → brain off, colliders off); off-screen NPCs run reduced or no logic.
9. **One damage funnel** — hook `Damage`/`FindDamage`/`ChangeHealth`, not individual weapons. `9999` = blocked sentinel.
10. **`RelList2` is indexed by `agentID`** inside try/catch — bad IDs fail silently.
11. **The Goal `*2` API in Brain.cs is dead** — use `Goals`/`SubGoals`.
12. **Big string-switches to know:** `AgentInteractions.DetermineButtons`/`PressedButton` (button text), `Relationships.SetupRelationshipOriginal` (agentName pairs), `BrainUpdate.MyUpdate` `losCheckAtIntervals` (Hobo/Cannibal/Thief/Vampire), `PlayfieldObject.FindDamage` (damage source), `Relationship.SetRelType` (rel strings).
