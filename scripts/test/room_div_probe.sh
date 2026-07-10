#!/bin/bash
# Reproduce the e2e gate's opening (claimer + follower joining one room) and
# dump both instances' generation state for divergence diagnosis. Requires
# wrangler dev on :8787.
set -u
ROOM="${1:-DIV$RANDOM}"
. "$(dirname "$0")/proton_env.sh"

cmdi() { # <inst> <command>
  local B; B="$(bepinex_dir "$1")"
  : > "$B/ep_out.txt"; printf '%s\n' "$2" > "$B/ep_cmd.txt"
  for _ in $(seq 1 30); do
    [ -f "$B/ep_cmd.txt" ] || { sleep 1; cat "$B/ep_out.txt" 2>/dev/null; return 0; }
    sleep 1
  done
  echo "(timeout: $2)"
}
waitlog() { for _ in $(seq 1 "$3"); do grep -aq "$2" "$(bepinex_dir "$1")/LogOutput.log" 2>/dev/null && return 0; sleep 2; done; return 1; }

trap kill_sor EXIT
sor_running && { echo "instances already running"; exit 2; }
for i in ecs0 ecs1; do
  make_win_clone $i >/dev/null
  rm -f "$(bepinex_dir $i)/LogOutput.log" "$(bepinex_dir $i)/ep_out.txt" "$(bepinex_dir $i)/ep_cmd.txt"
done

launch_win ecs0 --env=SOR_TEST_MODE=solo --env=SOR_TEST_NAME=DIVA \
  --env=SOR_ECS_ROOM="$ROOM" --env=SOR_ECS_SERVER=ws://127.0.0.1:8787 --env=SOR_ECS_NAME=DIVA \
  -- -batchmode -nographics >/dev/null 2>&1
waitlog ecs0 "claiming room world seed" 150 || { echo "A never claimed"; exit 1; }
launch_win ecs1 --env=SOR_TEST_MODE=solo --env=SOR_TEST_NAME=DIVB \
  --env=SOR_ECS_ROOM="$ROOM" --env=SOR_ECS_SERVER=ws://127.0.0.1:8787 --env=SOR_ECS_NAME=DIVB \
  -- -batchmode -nographics >/dev/null 2>&1

for i in ecs0 ecs1; do
  waitlog $i "state=in-game" 150 || echo "$i never in-game"
done
sleep 10
for i in ecs0 ecs1; do
  echo "===== $i"
  grep -a "Forcing map seed" "$(bepinex_dir $i)/LogOutput.log"
  cmdi $i worldhash | grep -v '^>'
  cmdi $i state | grep -v '^>' | head -3
  cmdi $i feelings 2>/dev/null | grep -v '^>' | head -2
done
