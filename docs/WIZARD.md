# WizardMod — the Wizard character

A standalone BepInEx plugin (`WizardMod/`) that adds a new playable
character, the **Wizard**, to Streets of Rogue. It does NOT depend on
EightPlayers (or anything else); the two mods coexist fine.

## What it adds

**Wizard** appears as the last slot on the character select screen,
always unlocked.

- **Glass cannon**: Strength 1, Endurance 1 (80 HP), Accuracy 3, Speed 3.
  Starts with a Knife. Reuses the Vampire body with purple legs
  ("robes") — no custom art needed for the agent itself.
- **Chaos Magic** (special ability, custom icon, 4 s cooldown) — every
  press casts a *random* spell, fired wherever the player aims:
  | roll | spell | implementation |
  |---|---|---|
  | 0 | Fireball | `gun.spawnBullet(bulletStatus.Fireball)` |
  | 1 | Frost bolt | `bulletStatus.FreezeRay` |
  | 2 | Lightning | `bulletStatus.Taser` |
  | 3 | Shrink ray | `bulletStatus.Shrink` |
  | 4 | Spirit blast | `bulletStatus.GhostBlaster` |
  | 5 | Sleep dart | `bulletStatus.Tranquilizer` |
  | 6 | Blink | teleport to a valid tile 3–8 away |
  | 7 | **Wild Surge** | random self-effect: Giant / Fast / InvisibleLimited / Shrunk (12/10/8/10 s) or a long random teleport (8–14) |

  The wizard shouts each spell (`Agent.Say`), e.g. "WILD SURGE! I AM MIGHTY!".

## How it works (all Harmony patches, no RogueLibs)

**RogueLibs was evaluated and rejected**: v4.0.0-rc.1 (the final release;
repo archived Dec 2024) is compiled against the pre-2025 game and
references `com.unity.multiplayer-hlapi.Runtime`, which the current
Unity-2022.3.60 build no longer ships — every one of its item/ability
patches fails with IL-compile FileNotFoundException. Everything is
hand-rolled instead, mirroring vanilla code paths found in `decompiled/`
(see HANDOFF.md for how to regenerate that folder).

`WizardCharacter.cs` — injects the character:
- `CharacterSelect.RealAwake` postfix: adds "Wizard" to
  `slotAgentTypes`/`slotAgentTypesComplete`; aliases
  `gameResources.bodyDic["WizardS"] = bodyDic["VampireS"]` (and
  `bodyGDic`) for the select-screen portrait.
- `Unlocks.LoadInitialUnlocks` postfix: `AddUnlock("Wizard","Agent",true)`
  + manual add to `sessionDataBig.agentUnlocks` (the per-type fan-out
  runs before the postfix).
- `NameDB.GetName` prefix: name/description for agent + ability item
  (types "Agent", "Description", "Item"; empty "_BQ" big-quest text).
- `Agent.SetupAgentStats` postfix: stats, Knife, purple `legsColor`,
  `statusEffects.GiveSpecialAbility("ChaosMagic")`.
- `AgentHitbox.SetupBodyStrings` postfix: rewrites in-world body sprite
  names `Wizard<Dir>` → `Vampire<Dir>`.

`ChaosMagic.cs` — the ability, modeled exactly on vanilla MindControl
(`StatusEffects.PressedSpecialAbility` / `RechargeSpecialAbility2`):
- `InvItem.SetupDetails` postfix: item definition (sprite from embedded
  `Resources/ChaosMagic.png` injected into `gameResources.itemDic`,
  `initCount=0`, `stackable`, `lowCountThreshold=100`).
- `StatusEffects.PressedSpecialAbility` prefix: when
  `agent.specialAbility == "ChaosMagic"`, handles cooldown
  (`invItemCount`, 0 = ready), starts a recharge coroutine (1/s
  countdown, "Recharged" buff text, HUD slot MakeUsable), casts the
  random spell. All HUD touches are try/catch-guarded (headless/remote
  players have no buffDisplay — this NRE'd and wedged the cooldown until
  guarded).

### Multiplayer
Effects go exclusively through the game's own synced mechanisms
(player-fired bullets, self-teleport, self status effects), so nothing
custom crosses the wire. The character name string is passed verbatim
by Mirror with no whitelist. **Every machine needs the mod installed** —
without it a peer would see a stats-less agent with a broken body.
(The ability int-whitelist `ObjectMultAgent.convertSpecialAbilityToInt`
maps unknown abilities to 0; irrelevant here because each client derives
the ability locally from the agent name in `SetupAgentStats`, same as
vanilla characters. Verified working host-side; untested with a remote
client as the wizard.)

## Building

```sh
cd WizardMod && ~/.dotnet/dotnet build -c Release
cp bin/Release/net472/WizardMod.dll \
  "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue/BepInEx/plugins/"
```

## Install bundles

`dist/SoR-WizardMod-Windows.zip` / `-Linux.zip` — BepInEx 5.4 + WizardMod
+ INSTALL-README. Extract into the game folder. Standalone; to combine
with 8-player games also drop `EightPlayers.dll` into `BepInEx/plugins`.

## Testing (headless e2e)

TestDriver gained two env vars for this:
- `SOR_TEST_CHAR=Wizard` — forces that agent on the select screen
  (`curSelected[0]` before `AcceptChoice`)
- `SOR_TEST_CAST=1` — presses the special ability every 5 s and reports
  `agent=... ability=... abilityCount=... pos=...`
- `SOR_TEST_ACCEPT_DELAY=N` — leave select screen open N s (screenshots)

Verified 2026-07-08 (headless host, 90 s): spawns as Wizard with
ChaosMagic, `pressed-ability used=True` every cycle, cooldown counts
4→0, blinks move the agent, log shows all spell types rolling. GUI run
screenshot shows Wild Surge → Giant with speech bubble.

Note: headless level load hangs occasionally (~1 in 3 runs, stream stops
after `pressed-host`, zero exceptions) — pre-existing harness flakiness,
just re-run.
