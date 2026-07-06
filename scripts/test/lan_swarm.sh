#!/bin/bash
# End-to-end test: launch a headless LAN host + N clients of Streets of Rogue
# (inside the Steam flatpak, with BepInEx + EightPlayers + SorTestDriver)
# and let the test driver auto-host/auto-join. Reports land in $RD/inst*/report.log
#
# The game is built with Unity's Force Single Instance: it flocks a unity.lock file
# NEXT TO THE EXECUTABLE. Each instance therefore runs from its own clone directory
# containing a hard-linked executable (run_bepinex.sh resolves symlinks, so a
# symlinked exe would point the lock back at the real dir) and symlinked game data.
set -u

CLIENTS="${1:-5}"                      # players total = CLIENTS + 1
GAME="$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue"
RD="$HOME/.var/app/com.valvesoftware.Steam/data/sor-test"
CLONES="$HOME/.var/app/com.valvesoftware.Steam/data/sor-clones"

rm -rf "$RD"
mkdir -p "$RD"

make_clone() { # $1=name  -> clone dir under $CLONES
    local c="$CLONES/$1"
    [ -d "$c" ] && return 0
    mkdir -p "$c/BepInEx"
    ln    "$GAME/StreetsOfRogueLinux.x86_64" "$c/StreetsOfRogueLinux.x86_64"
    ln -s "$GAME/UnityPlayer.so"             "$c/UnityPlayer.so"
    ln -s "$GAME/StreetsOfRogueLinux_Data"   "$c/StreetsOfRogueLinux_Data"
    ln -s "$GAME/Character Pack"             "$c/Character Pack"
    ln -s "$GAME/libdoorstop.so"             "$c/libdoorstop.so"
    cp    "$GAME/run_bepinex.sh"             "$c/run_bepinex.sh"
    cp    "$GAME/steam_appid.txt"            "$c/steam_appid.txt" 2>/dev/null || true
    ln -s "$GAME/BepInEx/core"               "$c/BepInEx/core"
    ln -s "$GAME/BepInEx/plugins"            "$c/BepInEx/plugins"
    mkdir -p "$c/BepInEx/config"
    cp -r "$GAME/BepInEx/config/." "$c/BepInEx/config/" 2>/dev/null || true
    chmod +x "$c/run_bepinex.sh"
}

launch() { # $1=instance-dir  $2=mode  $3=name
    mkdir -p "$RD/$1"
    make_clone "$1"
    flatpak run --command=sh \
        --env=SOR_TEST_MODE="$2" \
        --env=SOR_TEST_NAME="$3" \
        --env=SOR_TEST_PORT=7777 \
        --env=SOR_TEST_ADDR=127.0.0.1 \
        --env=SOR_TEST_REPORT="$RD/$1/report.log" \
        com.valvesoftware.Steam -c \
        "C=\"\$HOME/.var/app/com.valvesoftware.Steam/data/sor-clones/$1\"; cd \"\$C\" && exec ./run_bepinex.sh -batchmode -nographics" \
        > "$RD/$1/stdout.log" 2>&1 &
    echo "launched $1 ($2, $3) pid=$!"
}

launch inst0 host Host1
sleep 40
for i in $(seq 1 "$CLIENTS"); do
    launch "inst$i" client "Kid$i"
    sleep 6
done

echo "all launched; reports in $RD/inst*/report.log"
