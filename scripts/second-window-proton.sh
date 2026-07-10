#!/bin/bash
# Launch an EXTRA Streets of Rogue window on this computer using the WINDOWS
# build through Proton, for local split-screen when the main client is also
# running under Proton (see scripts/second-window.sh for the native-Linux
# equivalent, which is the simpler option if the main client is native).
#
# Bypasses Steam entirely (runs `proton run` directly against the existing
# compatdata prefix), so the game switches to platform-less mode (no Steamworks)
# and joins via the LAN menu: Multiplayer -> LAN -> host's IP -> Join.
#
# The game locks unity.lock next to the executable, so this creates a
# lightweight clone directory with a hard-linked .exe and symlinked data,
# then runs it straight through Proton instead of through Steam.
#
# Usage: ./second-window-proton.sh [name] [pad-number]
#   ./second-window-proton.sh                 -> winwindow2, uses gamepad 2
#   ./second-window-proton.sh window3 3       -> third window, uses gamepad 3
set -u

NAME="${1:-winwindow2}"
PAD="${2:-2}"
STEAM="$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
GAME="$STEAM/steamapps/common/Streets of Rogue"
PROTON="$STEAM/steamapps/common/Proton 9.0 (Beta)/proton"
COMPATDATA="$STEAM/steamapps/compatdata/512900"
CLONES="$STEAM/data/sor-clones-win"
C="$CLONES/$NAME"

if [ ! -d "$C" ]; then
    mkdir -p "$C/BepInEx"
    ln    "$GAME/StreetsOfRogue.exe"        "$C/StreetsOfRogue.exe"
    ln -s "$GAME/UnityPlayer.dll"           "$C/UnityPlayer.dll"
    ln -s "$GAME/winhttp.dll"               "$C/winhttp.dll"
    ln -s "$GAME/StreetsOfRogue_Data"       "$C/StreetsOfRogue_Data"
    ln -s "$GAME/Character Pack"            "$C/Character Pack"
    ln -s "$GAME/MonoBleedingEdge"          "$C/MonoBleedingEdge"
    ln -s "$GAME/UnityCrashHandler64.exe"   "$C/UnityCrashHandler64.exe"
    cp    "$GAME/doorstop_config.ini"       "$C/doorstop_config.ini" 2>/dev/null || true
    cp    "$GAME/steam_appid.txt"           "$C/steam_appid.txt" 2>/dev/null || true
    ln -s "$GAME/BepInEx/core"              "$C/BepInEx/core"
    ln -s "$GAME/BepInEx/plugins"           "$C/BepInEx/plugins"
    mkdir -p "$C/BepInEx/config"
    cp -r "$GAME/BepInEx/config/." "$C/BepInEx/config/" 2>/dev/null || true
fi

exec flatpak run --command=sh \
    --env=STEAM_COMPAT_DATA_PATH="$COMPATDATA" \
    --env=STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM" \
    --env=SOR_PAD="$PAD" \
    com.valvesoftware.Steam -c \
    "cd \"$C\"; exec \"$PROTON\" run ./StreetsOfRogue.exe -screen-fullscreen 0"
