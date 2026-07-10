#!/bin/bash
# Launch Streets of Rogue (Windows build, via Proton) for local split-screen
# testing. Each run picks the NEXT connected gamepad (wrapping back to the
# first one once you run past the last), and gives that pad its own
# persistent clone dir + Wine prefix so the instances don't collide over
# Windows-side single-instance locks (which live in the shared Wine prefix's
# AppData, not next to the .exe like the native Linux build).
#
# Usage: just run it — no args needed. Run it once per player.
#   ./scripts/start.sh
#   ./scripts/start.sh            (again, in another terminal, for player 2)
#
# State (which pad is "next") is tracked in $CLONES/.next-pad.
set -u

STEAM="$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
GAME="$STEAM/steamapps/common/Streets of Rogue"
PROTON="$STEAM/steamapps/common/Proton 9.0 (Beta)/proton"
CLONES="$STEAM/data/sor-clones-win"
STATE="$CLONES/.next-pad"

mkdir -p "$CLONES"

# Which gamepad / instance slot to use. Each run advances to the next pad and
# wraps after the last, so running the script N times gives you N windows bound
# to pads 1..N. We do NOT auto-count /dev/input/js* because 8BitDo pads sleep
# (and don't enumerate on the host anyway) — a napping pad would misassign
# slots. Instead the count is fixed (SOR_PADS, default 2), and you can force a
# specific pad by passing it as the first argument:  ./scripts/start.sh 2
MAXPADS="${SOR_PADS:-2}"
if [ -n "${1:-}" ]; then
    PAD="$1"
else
    LAST=$(cat "$STATE" 2>/dev/null || echo 0)
    PAD=$(( (LAST % MAXPADS) + 1 ))
fi
echo "$PAD" > "$STATE"

NAME="pad$PAD"
C="$CLONES/$NAME"
PREFIX="$C/prefix"

echo "Using pad $PAD of $MAXPADS (instance: $NAME) — wraps after pad $MAXPADS"

if [ ! -d "$C/game" ]; then
    mkdir -p "$C/game/BepInEx"
    ln    "$GAME/StreetsOfRogue.exe"        "$C/game/StreetsOfRogue.exe"
    ln -s "$GAME/UnityPlayer.dll"           "$C/game/UnityPlayer.dll"
    ln -s "$GAME/winhttp.dll"               "$C/game/winhttp.dll"
    ln -s "$GAME/StreetsOfRogue_Data"       "$C/game/StreetsOfRogue_Data"
    ln -s "$GAME/Character Pack"            "$C/game/Character Pack"
    ln -s "$GAME/MonoBleedingEdge"          "$C/game/MonoBleedingEdge"
    ln -s "$GAME/UnityCrashHandler64.exe"   "$C/game/UnityCrashHandler64.exe"
    cp    "$GAME/doorstop_config.ini"       "$C/game/doorstop_config.ini" 2>/dev/null || true
    # NOTE: deliberately NOT copying steam_appid.txt. With it present, the
    # Windows build tries to init Steamworks against the already-running Steam
    # client, and Proton's Steam IPC shim fatally asserts (pipes.cpp) when
    # launched outside Steam's normal chain. Without it the game runs
    # platform-less and joins via LAN, exactly like the native second-window.sh.
    ln -s "$GAME/BepInEx/core"              "$C/game/BepInEx/core"
    ln -s "$GAME/BepInEx/plugins"           "$C/game/BepInEx/plugins"
    mkdir -p "$C/game/BepInEx/config"
    cp -r "$GAME/BepInEx/config/." "$C/game/BepInEx/config/" 2>/dev/null || true
fi
mkdir -p "$PREFIX"

# Seed this instance's isolated prefix with a COPY of your real save data
# (unlocked characters, progression, custom Characters) from the main Steam
# prefix. Without this the instance boots a blank save and none of your
# unlocked or modded characters appear. Copy (not symlink) so two instances
# writing at once can't corrupt your real save. Only seeds once per prefix.
MAINSAVE="$STEAM/steamapps/compatdata/512900/pfx/drive_c/users/steamuser/Documents/Streets of Rogue"
INSTDOCS="$PREFIX/pfx/drive_c/users/steamuser/Documents"
if [ -d "$MAINSAVE" ] && [ ! -d "$INSTDOCS/Streets of Rogue" ]; then
    echo "Seeding save data from main prefix (unlocks + custom characters)..."
    mkdir -p "$INSTDOCS"
    cp -r "$MAINSAVE" "$INSTDOCS/" 2>/dev/null && echo "  save seeded ($(du -sh "$INSTDOCS/Streets of Rogue" | cut -f1))"
fi

exec flatpak run --command=sh \
    --env=STEAM_COMPAT_DATA_PATH="$PREFIX" \
    --env=STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM" \
    --env=WINEDLLOVERRIDES="winhttp=n,b" \
    --env=SOR_PAD="$PAD" \
    com.valvesoftware.Steam -c \
    "cd \"$C/game\"; exec \"$PROTON\" run explorer /desktop=sor$PAD,1280x720 StreetsOfRogue.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720"
