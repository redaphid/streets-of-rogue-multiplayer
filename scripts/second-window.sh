#!/bin/bash
# Launch an EXTRA Streets of Rogue window on this computer, so a second (or third)
# local player can join the same online/LAN game — poor-man's split screen.
#
# The extra window runs without Steam (the mod detects this and switches the game
# to platform-less mode), so it joins games via the LAN menu:
#   Multiplayer -> LAN -> enter the host's IP -> Join.
#
# The game normally allows only one running copy (it locks unity.lock next to the
# executable), so this creates a lightweight clone directory with a hard-linked
# executable and symlinked data, then starts the game from there.
#
# Usage: ./second-window.sh [name] [pad-number]
#   ./second-window.sh                 -> window2, uses gamepad 2
#   ./second-window.sh window3 3       -> third window, uses gamepad 3
#
# pad-number binds the window to ONE gamepad (1 = first pad connected).
# The Steam-launched main window should get SOR_PAD=1 via its launch options:
#   SOR_PAD=1 ./run_bepinex.sh %command%
set -u

NAME="${1:-window2}"
PAD="${2:-2}"
GAME="$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue"
CLONES="$HOME/.var/app/com.valvesoftware.Steam/data/sor-clones"
C="$CLONES/$NAME"

if [ ! -d "$C" ]; then
    mkdir -p "$C/BepInEx"
    ln    "$GAME/StreetsOfRogueLinux.x86_64" "$C/StreetsOfRogueLinux.x86_64"
    ln -s "$GAME/UnityPlayer.so"             "$C/UnityPlayer.so"
    ln -s "$GAME/StreetsOfRogueLinux_Data"   "$C/StreetsOfRogueLinux_Data"
    ln -s "$GAME/Character Pack"             "$C/Character Pack"
    ln -s "$GAME/libdoorstop.so"             "$C/libdoorstop.so"
    cp    "$GAME/run_bepinex.sh"             "$C/run_bepinex.sh"
    cp    "$GAME/steam_appid.txt"            "$C/steam_appid.txt" 2>/dev/null || true
    ln -s "$GAME/BepInEx/core"               "$C/BepInEx/core"
    ln -s "$GAME/BepInEx/plugins"            "$C/BepInEx/plugins"
    mkdir -p "$C/BepInEx/config"
    cp -r "$GAME/BepInEx/config/." "$C/BepInEx/config/" 2>/dev/null || true
    chmod +x "$C/run_bepinex.sh"
fi

exec flatpak run --command=sh --env=SOR_PAD="$PAD" com.valvesoftware.Steam -c \
    "cd \"\$HOME/.var/app/com.valvesoftware.Steam/data/sor-clones/$NAME\" 2>/dev/null || cd \"$C\"; exec ./run_bepinex.sh -screen-fullscreen 0"
