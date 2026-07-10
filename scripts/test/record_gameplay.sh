#!/bin/bash
# Record a short clip of real gameplay from a single windowed instance,
# driven entirely through the command channel — the quick "does the game
# still look right" evidence artifact for each migration plank
# (docs/ecs-migration-plan.md step 6). For two-instance sync footage use
# E2E_VIDEO=1 scripts/test/e2e_scenario.sh instead.
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

cleanup() { kill_sor; }
trap cleanup EXIT

sor_running && { echo "game instances already running"; exit 2; }
mkdir -p "$OUT"
make_win_clone $INST
rm -f "$(bepinex_dir $INST)/LogOutput.log" "$(outf)" "$(cmdf)"
rm -rf "$(clone_dir $INST)/rec"; mkdir -p "$(clone_dir $INST)/rec"

envs=(--env=SOR_TEST_MODE=solo --env=SOR_TEST_NAME=Rec1)
[ -n "${SOR_ECS_ROOM:-}" ] && envs+=(--env=SOR_ECS_ROOM="$SOR_ECS_ROOM" \
  --env=SOR_ECS_SERVER="${SOR_ECS_SERVER:-ws://127.0.0.1:8787}" --env=SOR_ECS_NAME=Rec1)

echo "booting windowed instance..."
launch_win $INST "${envs[@]}" -- \
  -screen-fullscreen 0 -window-mode windowed -screen-width 960 -screen-height 540 -popupwindow \
  > /dev/null 2>&1

for i in $(seq 1 120); do
  grep -aq 'state=in-game.*loadComplete=True' "$(bepinex_dir $INST)/LogOutput.log" 2>/dev/null && break
  sleep 2
done
grep -aq 'state=in-game.*loadComplete=True' "$(bepinex_dir $INST)/LogOutput.log" \
  || { echo "instance never reached in-game"; exit 1; }
echo "in-game; recording ${SECS}s @ ${FPS}fps"

cmd "record $SECS $FPS rec" >/dev/null

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

# wait for the recorder to finish, then encode
for _ in $(seq 1 30); do
  grep -aq 'recording done' "$(bepinex_dir $INST)/LogOutput.log" && break
  sleep 2
done
kill_sor
FRAMES="$(clone_dir $INST)/rec"
N=$(ls "$FRAMES" 2>/dev/null | wc -l)
[ "$N" -gt 0 ] || { echo "no frames captured"; exit 1; }
ffmpeg -y -framerate $FPS -i "$FRAMES/f%05d.png" -c:v libx264 -pix_fmt yuv420p \
  "$OUT/$NAME.mp4" </dev/null >/dev/null 2>&1
rm -rf "$FRAMES"
echo "wrote $OUT/$NAME.mp4 ($N frames)"
