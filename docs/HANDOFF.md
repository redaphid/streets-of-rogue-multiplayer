# Project handoff / status

## NEW (2026-07-08): WizardMod — standalone Wizard character

`WizardMod/` adds a playable **Wizard** (glass cannon, Chaos Magic =
random spell per press). Fully verified headless + GUI screenshot;
dist zips built. **No dependency on EightPlayers.** Details, patch map,
and the RogueLibs post-mortem (it's dead against this Unity-2022 build):
see `docs/WIZARD.md`. TestDriver gained `SOR_TEST_CHAR`, `SOR_TEST_CAST`,
`SOR_TEST_ACCEPT_DELAY` for character/ability e2e tests.

**Goal:** Let up to 8 people play Streets of Rogue in ONE game, across a
heterogeneous mix of computers — some machines running several game windows
("split screen"), each window driven by its own gamepad, all joined over LAN.

Game: Flatpak Steam, Linux, at
`~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue`
(Unity 2022.3.60f1, Mono backend, Mirror networking, Rewired input, AppID 512900).

## What works (verified)

- **8-player cap raised** — host + 7 headless clients all reach `loadComplete`
  seeing 8 agents. Patches: `NetworkSlots_Patch` (pads per-slot lists,
  `maxConnections`), `ServerFull_Patch` (transpiler rewriting the two
  hardcoded `4`s in `NetworkManagerUWP.WaitUntilLoadComplete`),
  `PlayerLimitButton_Patch`.
- **LAN menu re-enabled** (`LanMenu_Patch`) — direct IP host/join, port 7777.
- **Multiple windows per PC** — Unity's single-instance flock (`unity.lock`
  lives NEXT TO the exe) bypassed via clone dirs with a **hard-linked** exe +
  symlinked data. See `scripts/second-window.sh [name] [pad-number]`.
- **One gamepad per window** — `SOR_PAD=N` (1-based) env var;
  `JoystickBinding.cs` strips all but the Nth joystick from Rewired players.
- **Platform-less fallback** — when Steam can't initialize (second windows,
  test clones), Galaxy/GOG fallback is suppressed (it DllNotFound-storms and
  wedges loading). `NoSteamFallback_Patch` + `NoGalaxy*_Patch`.
- **8BitDo Zero 2 auto-mapping** — `ZeroTwoMapping.cs` detects pads whose
  name/hardwareName contains "8bitdo zero" and installs a compact layout
  (D-pad move/menus, face buttons by PRINTED Nintendo labels, L inventory,
  R next item, Select health, Start menu). 16/16 bindings verified installed.
- **Gamepad window forcing** — this build hides the Controller Type settings
  button, so `sessionDataBig.player1Controller` stays "Keyboard", which
  disables the joystick "Gamepad" map and gates off `MoveXJ` reads
  (`PlayerControl.cs:2100`, gated by `controllerType != "Keyboard"`).
  `GamepadWindow.ForceGamepadPlayer1()` forces "Gamepad" when `SOR_PAD` is
  set. Confirmed working via CTRLDBG (`p1=Gamepad agent=Gamepad Gamepad=on`).
- **Steam launch options** (Flatpak expands `%command%` into a wrapper chain
  the stock script can't parse; `#` comments it out):
  `SOR_PAD=1 ./run_bepinex.sh # %command%`
- **Steam Input must be disabled** per-game (Properties → Controller) or
  Steam grabs the pad exclusively (diagnosed with `fuser /dev/input/event*`).

## OPEN BUG — the active task

**D-pad moves menus but NOT the character.** `MoveXJ`/`MoveYJ` axes read 0.0
(both `GetAxis` and `GetAxisRaw`) even though `MenuUpJ`-style *button*
bindings on the SAME D-pad elements work. The MoveXJ element maps ARE
present (CTRLDBG shows `X<-el18/P/B` etc.).

Leading suspects:
1. Button-type element maps with `Pole` axis contributions may not feed
   `GetAxis` for hat/D-pad elements — try binding the hat **axis** directly
   (`bindaxis MoveXJ D-Pad` or the hat X/Y axis element) via the command
   channel.
2. The game rebuilds controller maps (`ChangeKeys`/`TransferToMyScheme`)
   and may reinstall its own map that shadows ours.
3. Note: hat directions never appear in the CTRLDBG raw `GetButtonById`
   dump — element ids ≤21 only, and hats aren't raw buttons; that part is
   expected, not evidence.

The **live command channel** (below) was built specifically to resolve this
without restarting the game. Plan: user stands in a level holding D-pad
right; send `dump` + `action MoveXJ` + `action MenuRightJ`; compare; then
try `bindaxis MoveXJ <hat axis element>` live.

## Live debugging tools

All paths relative to the game dir's `BepInEx/`.

- **CTRLDBG stream** — once/second change-only state line in
  `LogOutput.log`: p1 setting, agent controllerType, joystick counts, map
  category on/off, `move=` axes + raw, MoveXJ/MenuUpJ element-map dumps
  (`X<-el18/P/B`), menu button states, raw pressed elements.
- **Command channel** — write lines to `BepInEx/ep_cmd.txt` (polled every
  0.5 s, file deleted after read); results append to `BepInEx/ep_out.txt`
  and to the log as `EPCMD`. Commands:
  `dump` · `action <name>` · `bind <action> <+|-> <element name>` ·
  `bindaxis <action> <element name>` · `unbind <action>` · `remap` ·
  `nintendo on|off` · `enable <category> on|off`
- Monitor with:
  `tail -F "<gamedir>/BepInEx/LogOutput.log" | grep -E 'EPCMD|CTRLDBG|ZeroTwo|Error|Exception'`

## Building & installing

```sh
cd EightPlayers && ~/.dotnet/dotnet build -c Release
cp bin/Release/net472/EightPlayers.dll \
  "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue/BepInEx/plugins/"
```

Targets net472 with direct references to game DLLs (`Private=false`).
`decompiled/` holds the full ilspycmd output of the game (gitignored,
copyrighted — regenerate with ilspycmd 8.2.0.7535,
`DOTNET_ROLL_FORWARD=LatestMajor`). Key studied files: `PlayerControl.cs`
(movement gate ~2085-2117, schemes 1504-1726, action arrays 940-1010),
`NetworkManagerUWP.cs`, `MenuGUI.cs`, `Agent.cs`, `Movement.cs`.

## Testing

- `scripts/test/lan_swarm.sh` — 6 headless LAN instances via clone dirs.
  Needs `TestDriver` plugin (SOR_TEST_MODE-gated auto host/join; also sets
  Mirror `headlessStartMode = HeadlessStartOptions.DoNothing`, else every
  batchmode instance self-hosts on 7777).
- Runs must happen inside the flatpak (host glibc 2.35 < doorstop's 2.38).

## Distribution

`dist/SoR-EightPlayers-Windows.zip` / `-Linux.zip` — BepInEx + plugin +
INSTALL-README. **The zips are STALE** (pre-CommandChannel/Nintendo-labels);
`dist/EightPlayers.dll` is current. Rebuild zips once controls are fixed.

## Pending

1. **Fix the movement bug** (above) — drive the command channel live.
2. User-verify Nintendo-label face buttons feel right.
3. Rebuild dist zips after controls finalized.
4. Push to GitHub (no remote yet; `gh repo create` was previously denied by
   sandbox permissions — user may need to create the repo or grant access).
5. Phase 2 (stretch): true split-screen players inside online games — keep
   `coopMode`/`fourPlayerMode` during `multiplayerMode`, `NetworkServer.Spawn`
   extra local agents; clients already treat spawned agents with
   `playerColor != 0` named "PlayerrX" as remote players (Agent.cs ~11834).
