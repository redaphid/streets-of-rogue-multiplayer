# Shared launcher for Streets of Rogue test instances — WINDOWS build via
# Proton. Source this from test scripts; do not execute it.
#
# Steam replaced the native Linux depot with the Windows one (Jul 2026), so
# every harness now boots StreetsOfRogue.exe through Proton inside the Steam
# flatpak. Key differences from the old native-Linux launcher:
#   - single-instance locks live in the Wine prefix's AppData, not next to
#     the exe -> every instance gets its OWN prefix dir (start.sh discovery)
#   - BepInEx injects via winhttp.dll (WINEDLLOVERRIDES), not run_bepinex.sh
#   - no steam_appid.txt in clones: Steamworks init under bare `proton run`
#     fatally asserts in Proton's Steam IPC shim; platform-less mode is what
#     the tests want anyway
#
# The BepInEx dir is real (per-instance logs + ep_cmd/ep_out command channel),
# with core symlinked and plugins COPIED from the main install at clone
# refresh so a mid-run redeploy of the main install can't corrupt a running
# instance's lazily-loaded Mono metadata.

STEAM="$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
GAME="$STEAM/steamapps/common/Streets of Rogue"
PROTON="$STEAM/steamapps/common/Proton 9.0 (Beta)/proton"
CLONES="$STEAM/data/sor-clones-win"

# Process-match pattern for pgrep/pkill -f. Brackets keep the pattern from
# matching its own pgrep/pkill command line (and our launching shell).
SOR_PROC='StreetsOfRogue[.]exe'

clone_dir()  { echo "$CLONES/$1/game"; }
bepinex_dir(){ echo "$CLONES/$1/game/BepInEx"; }

# make_win_clone <name> — create (or refresh plugins of) a Proton clone.
make_win_clone() {
    local c="$CLONES/$1/game"
    if [ ! -d "$c" ]; then
        mkdir -p "$c/BepInEx"
        ln    "$GAME/StreetsOfRogue.exe"      "$c/StreetsOfRogue.exe"
        ln -s "$GAME/UnityPlayer.dll"         "$c/UnityPlayer.dll"
        ln -s "$GAME/winhttp.dll"             "$c/winhttp.dll"
        ln -s "$GAME/StreetsOfRogue_Data"     "$c/StreetsOfRogue_Data"
        ln -s "$GAME/Character Pack"          "$c/Character Pack"
        ln -s "$GAME/MonoBleedingEdge"        "$c/MonoBleedingEdge"
        cp    "$GAME/doorstop_config.ini"     "$c/doorstop_config.ini" 2>/dev/null || true
        ln -s "$GAME/BepInEx/core"            "$c/BepInEx/core"
        mkdir -p "$c/BepInEx/config" "$c/BepInEx/plugins"
        cp -r "$GAME/BepInEx/config/." "$c/BepInEx/config/" 2>/dev/null || true
    fi
    mkdir -p "$CLONES/$1/prefix"
    # Seed the instance prefix with a COPY of the real save (settings +
    # dismissed first-run state + unlocks). A blank prefix boots into
    # first-run UI that wedges the test driver's menu automation.
    local mainsave="$STEAM/steamapps/compatdata/512900/pfx/drive_c/users/steamuser/Documents/Streets of Rogue"
    local instdocs="$CLONES/$1/prefix/pfx/drive_c/users/steamuser/Documents"
    if [ -d "$mainsave" ] && [ ! -d "$instdocs/Streets of Rogue" ]; then
        mkdir -p "$instdocs"
        cp -r "$mainsave" "$instdocs/"
    fi
    # Deterministic plugin set for tests: just the mod + the driver (the main
    # install may also carry CharacterCreator/WizardMod, which change the
    # roster and would perturb parity runs).
    rm -f "$c/BepInEx/plugins/"*.dll
    cp "$GAME/BepInEx/plugins/EightPlayers.dll"  "$c/BepInEx/plugins/"
    cp "$GAME/BepInEx/plugins/SorTestDriver.dll" "$c/BepInEx/plugins/"
}

# launch_win <name> <extra flatpak --env args...> -- <unity args...>
# Boots the clone through Proton in the background. Caller redirects output.
launch_win() {
    local name="$1"; shift
    local envs=()
    while [ $# -gt 0 ] && [ "$1" != "--" ]; do envs+=("$1"); shift; done
    [ "${1:-}" = "--" ] && shift
    local c="$CLONES/$name/game"
    flatpak run --command=sh \
        --env=STEAM_COMPAT_DATA_PATH="$CLONES/$name/prefix" \
        --env=STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM" \
        --env=WINEDLLOVERRIDES="winhttp=n,b" \
        "${envs[@]}" \
        com.valvesoftware.Steam -c \
        "cd \"$c\"; exec \"$PROTON\" run ./StreetsOfRogue.exe $*" &
}

kill_sor() { pkill -9 -f "$SOR_PROC" 2>/dev/null; }
sor_running() { pgrep -f "$SOR_PROC" >/dev/null; }
