# Streets of Rogue — EightPlayers mod

Play Streets of Rogue with **up to 8 people in one game**, using any mix of
computers — including two game windows side by side on one PC so two kids can
share a machine ("split screen").

Vanilla limits online games to 4 players and hides the LAN menu. This
BepInEx mod removes both limits.

## What the mod does

- Raises the online player cap from 4 to 8 (configurable up to 16):
  network player slots, the server-full kick, the host screen's player-limit
  button, and Mirror's connection cap.
- Re-enables the hidden **Multiplayer → LAN** menu (host/join by IP and port —
  no Steam lobby needed).
- If the game starts without Steam (e.g. an extra window launched outside
  Steam), it now runs in platform-less mode instead of crash-looping in the
  GOG Galaxy fallback. LAN play works fine there.

Config file (created after first run):
`BepInEx/config/com.hypnodroid.eightplayers.cfg` — `MaxPlayers`, `ShowLanMenu`.

## Layout of this project

- `EightPlayers/` — the mod source (BepInEx 5 plugin, Harmony patches)
- `TestDriver/` — test-only plugin that auto-hosts/auto-joins LAN games
  (inert unless `SOR_TEST_MODE` is set; safe to delete from `BepInEx/plugins`)
- `scripts/second-window.sh` — launch an extra game window on this PC
- `scripts/test/lan_swarm.sh` — automated 6-instance LAN test
- `decompiled/` — decompiled game source for reference

## Install (this Linux PC — already done)

BepInEx 5.4.23 + the plugin are installed in the game folder:
`~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue`

To make Steam start the game with the mod, set the game's **Launch Options**
(Steam → Streets of Rogue → Properties → General):

    ./run_bepinex.sh %command%

## Install (other PCs, e.g. the nephews' Windows machines)

1. Download BepInEx 5.4.23 x64 for Windows, unzip into the game folder
   (`...\steamapps\common\Streets of Rogue`, next to `StreetsOfRogue.exe`).
   On Windows no launch options are needed (BepInEx loads via `winhttp.dll`).
2. Copy `EightPlayers.dll` into `BepInEx\plugins\`.
3. Run the game once; check `BepInEx\LogOutput.log` mentions "EightPlayers loaded".

## How to play (8 players, mixed setup)

All computers must be on the same network (or VPN like Tailscale).

**Host** (fastest PC): Multiplayer → LAN → set a name and port `7777` → Host.
Find the host PC's LAN IP (e.g. `ip addr` / `ipconfig`).

**Everyone else**: Multiplayer → LAN → enter the host's IP and port → Join.

**Two or more players on one PC**: play the first window normally (via Steam),
then run `scripts/second-window.sh` for each extra window and join the game
from it via the LAN menu. Put the windows side by side.

**Controllers — one pad per window** (`SOR_PAD`): normally every window would
react to every connected gamepad. Setting the `SOR_PAD` environment variable
binds a window to exactly one pad (numbered in connection order):

- Main (Steam) window: set Launch Options to `SOR_PAD=1 ./run_bepinex.sh %command%`
  (or leave `SOR_PAD` off and play that window with keyboard+mouse).
- Extra windows: `scripts/second-window.sh window2 2`, `window3 3`, ... — the
  second argument is the pad number and is passed as `SOR_PAD` automatically.
- On Windows, launch the extra window from a `.bat` that does
  `set SOR_PAD=2` before starting the game.

Hosting through the normal Steam **Internet** flow also works with up to 8
players (the player-limit button now goes to 8), but LAN is the recommended
path for mixed multi-window setups.

## Building

Requires the .NET 8 SDK (`~/.dotnet` here):

    cd EightPlayers && dotnet build -c Release
    cp bin/Release/net472/EightPlayers.dll "<game>/BepInEx/plugins/"

## Known limitations

- Players 5-8 fall back to default UI colors in a few places (cosmetic).
- True single-window split screen inside an online game is not supported by
  the base game; that is the planned "Phase 2" of this mod (spawning extra
  local players as networked agents). Until then, use one window per player.
- The Windows build enforces single-instance differently; if a second window
  won't start there, keep one window per Windows PC (or ask me to dig into it).
