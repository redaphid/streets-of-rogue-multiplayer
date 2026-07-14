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
    # Plugin dependencies (e.g. MoonSharp for the Lua behavior engine) must
    # ride along or BepInEx can't load the mod's behavior features.
    cp "$GAME/BepInEx/plugins/MoonSharp.Interpreter.dll" "$c/BepInEx/plugins/" 2>/dev/null || true
    # The GM instance ('gm') is NOT a parity/test run — it wants the custom
    # characters (the Wizard, etc.) and their big-quest fixes. Copy the
    # CharacterCreator mod + its data-driven Characters/ folder for it, so a
    # normal ./start.sh GM run loads them (test/parity clones stay pure above).
    if [ "$1" = "gm" ]; then
        cp "$GAME/BepInEx/plugins/CharacterCreator.dll" "$c/BepInEx/plugins/" 2>/dev/null || true
        rm -rf "$c/BepInEx/plugins/Characters"
        cp -r "$GAME/BepInEx/plugins/Characters" "$c/BepInEx/plugins/" 2>/dev/null || true
    fi
}

# launch_win <name> <extra flatpak --env args...> -- <unity args...>
# Boots the clone through Proton in the background. Caller redirects output.
# VDESK=<res> (e.g. VDESK=960x540) launches inside a Wine virtual desktop
# named after the instance — on this Wayland/XWayland session a plain
# windowed/-popupwindow Unity window is created but never MAPPED (audio, no
# picture; discovered by the main-branch agent with the user); the virtual
# desktop forces a real visible window and blocks exclusive-fullscreen
# grabs. Use for every headed/recorded instance; headless doesn't need it.
launch_win() {
    local name="$1"; shift
    local envs=()
    while [ $# -gt 0 ] && [ "$1" != "--" ]; do envs+=("$1"); shift; done
    [ "${1:-}" = "--" ] && shift
    local c="$CLONES/$name/game"
    local runline="exec \"$PROTON\" run ./StreetsOfRogue.exe $*"
    [ -n "${VDESK:-}" ] && runline="exec \"$PROTON\" run explorer /desktop=$name,$VDESK StreetsOfRogue.exe $*"
    flatpak run --command=sh \
        --env=STEAM_COMPAT_DATA_PATH="$CLONES/$name/prefix" \
        --env=STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM" \
        --env=WINEDLLOVERRIDES="winhttp=n,b" \
        "${envs[@]}" \
        com.valvesoftware.Steam -c \
        "cd \"$c\"; $runline" &
}

# Kills/queries ONLY harness-owned instances (anything under $CLONES except
# pad* — those are the user's interactive split-screen windows; the old
# global 'StreetsOfRogue[.]exe' pkill wiped them out from under live play).
# Match by process CWD: every process in an instance's tree (bwrap, sh,
# proton's python, wine, the game) inherits the clone dir as its cwd.
_harness_pids() {
    local p cwd
    for p in $(pgrep -f 'StreetsOfRogue|proton run' 2>/dev/null); do
        cwd=$(readlink "/proc/$p/cwd" 2>/dev/null) || continue
        case "$cwd" in
            "$CLONES"/pad*) ;;
            "$CLONES"/*) echo "$p";;
        esac
    done
}
kill_sor() { local pids; pids=$(_harness_pids); [ -n "$pids" ] && kill -9 $pids 2>/dev/null; true; }
sor_running() { [ -n "$(_harness_pids)" ]; }

# ---- host-side window recording -----------------------------------------
# The in-game ScreenCapture recorder writes nothing under Proton (the
# coroutine runs, no files appear anywhere in the prefix). Capture the X11
# window from the HOST instead: ffmpeg x11grab -window_id works on the
# XWayland windows wine creates (verified 2026-07-10; the old "x11grab is
# black" finding applied to full-root :0 grabs of the native build).

game_window_ids() { wmctrl -l 2>/dev/null | awk '/Streets of Rogue/{print $1}'; }

# wait_new_window <timeout-s> [known ids...] -> prints the first id not in the known set
wait_new_window() {
    local t="$1"; shift
    local known=" $* "
    for _ in $(seq 1 "$t"); do
        for id in $(game_window_ids); do
            case "$known" in *" $id "*) ;; *) echo "$id"; return 0;; esac
        done
        sleep 1
    done
    return 1
}

# start_x11_recording <window-id> <out.mp4> [fps] -> prints recorder pid
start_x11_recording() {
    local id="$1" out="$2" fps="${3:-10}"
    ffmpeg -y -f x11grab -window_id "$id" -framerate "$fps" -i "${DISPLAY:-:0}" \
        -c:v libx264 -preset veryfast -pix_fmt yuv420p "$out" </dev/null >/dev/null 2>&1 &
    echo $!
}

# stop_x11_recording <pid...> — SIGINT so ffmpeg finalizes the mp4 trailer
stop_x11_recording() { kill -INT "$@" 2>/dev/null; sleep 2; kill -9 "$@" 2>/dev/null; true; }
