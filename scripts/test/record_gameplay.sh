#!/bin/bash
# Record a short clip of real gameplay from a single windowed instance,
# driven entirely through the command channel — the quick "does the game
# still look right" evidence artifact for each migration plank
# (docs/ecs-migration-plan.md step 6). For two-instance sync footage use
# E2E_VIDEO=1 scripts/test/e2e_scenario.sh instead.
#
# Capture is host-side ffmpeg x11grab on the game's window (the in-game
# ScreenCapture recorder writes nothing under Proton).
#
# Usage: scripts/test/record_gameplay.sh [seconds] [outname]
#   SOR_ECS_ROOM=X SOR_ECS_SERVER=ws://... to record connected to a room.
# Produces outputs/recordings/<outname>.mp4 (default gameplay-<ts>.mp4).
set -u

SECS="${1:-30}"
FPS=10
NAME="${2:-gameplay-$(date +%Y%m%d-%H%M%S)}"
. "$(dirname "$0")/proton_env.sh"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$REPO/outputs/recordings"
INST=rec0

cmdf() { echo "$(bepinex_dir $INST)/ep_cmd.txt"; }
outf() { echo "$(bepinex_dir $INST)/ep_out.txt"; }
cmd() { # send one command, wait for consumption + output
  : > "$(outf)"
  printf '%s\n' "$*" > "$(cmdf)"
  for _ in $(seq 1 30); do
    [ -f "$(cmdf)" ] || { sleep 0.5; cat "$(outf)" 2>/dev/null; return 0; }
    sleep 1
  done
  echo "(timeout: $*)"; return 1
}

RECPID=""
cleanup() { [ -n "$RECPID" ] && stop_x11_recording "$RECPID"; kill_sor; }
trap cleanup EXIT

sor_running && { echo "game instances already running"; exit 2; }
mkdir -p "$OUT"
make_win_clone $INST
rm -f "$(bepinex_dir $INST)/LogOutput.log" "$(outf)" "$(cmdf)"

envs=(--env=SOR_TEST_MODE=solo --env=SOR_TEST_NAME=Rec1)
[ -n "${SOR_ECS_ROOM:-}" ] && envs+=(--env=SOR_ECS_ROOM="$SOR_ECS_ROOM" \
  --env=SOR_ECS_SERVER="${SOR_ECS_SERVER:-ws://127.0.0.1:8787}" --env=SOR_ECS_NAME=Rec1)

KNOWN="$(game_window_ids | tr '\n' ' ')"
echo "booting windowed instance..."
launch_win $INST "${envs[@]}" -- \
  -screen-fullscreen 0 -window-mode windowed -screen-width 960 -screen-height 540 -popupwindow \
  > /dev/null 2>&1

WIN=$(wait_new_window 120 $KNOWN) || { echo "window never appeared"; exit 1; }
wmctrl -i -r "$WIN" -t "$(wmctrl -d | awk '$2=="*"{print $1}')" 2>/dev/null
for i in $(seq 1 120); do
  grep -aq 'state=in-game.*loadComplete=True' "$(bepinex_dir $INST)/LogOutput.log" 2>/dev/null && break
  sleep 2
done
grep -aq 'state=in-game.*loadComplete=True' "$(bepinex_dir $INST)/LogOutput.log" \
  || { echo "instance never reached in-game"; exit 1; }
echo "in-game; recording window $WIN for ${SECS}s @ ${FPS}fps"
RECPID=$(start_x11_recording "$WIN" "$OUT/$NAME.mp4" $FPS)

# improvised gameplay: walk to a live NPC, swing, wander back and forth
POS=$(cmd state | grep 'player:' | grep -o 'pos=([^)]*)' | tr -d 'pos=()')
PX=${POS%%,*}; PY=${POS##*,}
NPCPOS=$(cmd npcs | grep 'dead=False' | head -1 | grep -o 'pos=([^)]*)' | tr -d 'pos=()')
[ -n "$NPCPOS" ] && cmd "walkto ${NPCPOS%%,*} ${NPCPOS##*,} 8" >/dev/null
sleep 5
cmd "hold attack 2" >/dev/null
sleep 3
[ -n "${PX:-}" ] && cmd "walkto $PX $PY 8" >/dev/null
sleep $((SECS > 12 ? SECS - 11 : 1))

stop_x11_recording "$RECPID"; RECPID=""
echo "wrote $OUT/$NAME.mp4"
