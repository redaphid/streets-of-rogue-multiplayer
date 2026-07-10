#!/bin/bash
# Minimal generation-determinism probe: boot N solo instances SEQUENTIALLY
# with the same SOR_SEED (no room, no adoption, no peers) and print each
# one's level-1 worldhash + seed. If these differ, same-seed generation is
# nondeterministic on this platform and no seed bookkeeping can fix e2e
# convergence — sync must adopt authority layout instead of regenerating.
# Usage: scripts/test/det_check.sh [runs] [seed]
set -u
RUNS="${1:-2}"
SEED="${2:-dettest1}"
. "$(dirname "$0")/proton_env.sh"

for n in $(seq 1 "$RUNS"); do
  I="det$n"
  make_win_clone "$I" >/dev/null
  B="$(bepinex_dir "$I")"
  rm -f "$B/LogOutput.log" "$B/ep_out.txt" "$B/ep_cmd.txt"
  launch_win "$I" --env=SOR_TEST_MODE=solo --env=SOR_TEST_NAME="Det$n" --env=SOR_SEED="$SEED" \
    -- -batchmode -nographics >/dev/null 2>&1
  for _ in $(seq 1 120); do
    grep -aq 'state=in-game.*loadComplete=True' "$B/LogOutput.log" 2>/dev/null && break
    sleep 2
  done
  sleep 5
  : > "$B/ep_out.txt"; printf 'worldhash\nstate\n' > "$B/ep_cmd.txt"
  for _ in $(seq 1 20); do grep -q 'worldhash' "$B/ep_out.txt" 2>/dev/null && break; sleep 1; done
  echo "run $n: $(grep -a 'worldhash' "$B/ep_out.txt" | head -1) | $(grep -a 'seed=' "$B/ep_out.txt" | head -1 | grep -o 'seed=[^ ]*')"
  kill_sor; sleep 3
done
