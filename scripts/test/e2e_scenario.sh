#!/bin/bash
# End-to-end ECS netcode scenario: boots two headless game instances against a
# local `wrangler dev` (ws://127.0.0.1:8787), plays a full co-op demo out, and
# asserts every synced system along the way. Consolidates the per-iteration
# verification chains into one repeatable regression suite.
#
# Usage: scripts/test/e2e_scenario.sh [ROOM]
# Prereqs: wrangler dev running (cd worker && npm run dev), EightPlayers.dll
# deployed to the game's BepInEx/plugins, clones ecs0/ecs1 present
# (lan_swarm.sh make_clone pattern), no game instances running.
set -u

ROOM="${1:-E2E$RANDOM}"
GAME="$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Streets of Rogue"
CL="$HOME/.var/app/com.valvesoftware.Steam/data/sor-clones"
RD="$HOME/.var/app/com.valvesoftware.Steam/data/sor-test/e2e"
PASS=0; FAIL=0

ok()   { echo "  ok - $1"; PASS=$((PASS+1)); }
fail() { echo "FAIL - $1"; FAIL=$((FAIL+1)); }
check(){ if eval "$1"; then ok "$2"; else fail "$2"; fi; }

# ---- helpers ------------------------------------------------------------
log()  { echo "$CL/$1/BepInEx/LogOutput.log"; }
cmdf() { echo "$CL/$1/BepInEx/ep_cmd.txt"; }
outf() { echo "$CL/$1/BepInEx/ep_out.txt"; }

# cmd <inst> <command...>  — run a command-channel command, wait for output
cmd() {
  local inst="$1"; shift
  : > "$(outf "$inst")"
  printf '%s\n' "$*" > "$(cmdf "$inst")"
  for _ in $(seq 1 30); do
    [ -s "$(outf "$inst")" ] && grep -q ">" "$(outf "$inst")" && { sleep 0.5; cat "$(outf "$inst")"; return 0; }
    sleep 1
  done
  echo "(timeout: $*)"; return 1
}

# waitlog <inst> <pattern> <timeout-s>
waitlog() {
  for _ in $(seq 1 "$3"); do
    grep -aq "$2" "$(log "$1")" 2>/dev/null && return 0
    sleep 1
  done
  return 1
}

launch() { # <inst> <name> <port>
  mkdir -p "$RD/$1"
  flatpak run --command=sh \
    --env=SOR_TEST_MODE=host --env=SOR_TEST_NAME="$2" --env=SOR_TEST_PORT="$3" --env=SOR_TEST_ADDR=127.0.0.1 \
    --env=SOR_TEST_REPORT="$RD/$1/report.log" \
    --env=SOR_ECS_ROOM="$ROOM" --env=SOR_ECS_SERVER=ws://127.0.0.1:8787 --env=SOR_ECS_NAME="$2" \
    com.valvesoftware.Steam -c \
    "C=\"$CL/$1\"; cd \"\$C\" && exec ./run_bepinex.sh -batchmode -nographics" \
    > "$RD/$1/stdout.log" 2>&1 &
}

player_uid() { cmd "$1" state | grep "player:" | grep -o 'uid=[0-9]*' | head -1 | cut -d= -f2; }

cleanup() { pkill -9 -f 'StreetsOfRogue[L]inux' 2>/dev/null; }
trap cleanup EXIT

# ---- scenario -----------------------------------------------------------
echo "e2e scenario in room $ROOM"
curl -sf -m 3 http://127.0.0.1:8787/ >/dev/null || { echo "wrangler dev not running"; exit 2; }
pgrep -f 'StreetsOfRogue[L]inux' >/dev/null && { echo "game instances already running"; exit 2; }
rm -f "$CL"/ecs0/BepInEx/LogOutput.log "$CL"/ecs1/BepInEx/LogOutput.log \
      "$CL"/ecs0/BepInEx/ep_out.txt "$CL"/ecs1/BepInEx/ep_out.txt

echo "[1/8] boot + shared world"
launch ecs0 E2EA 7777
waitlog ecs0 "claiming room world seed" 180 && ok "A claimed room seed" || fail "A claimed room seed"
SEED=$(grep -a "claiming room world seed" "$(log ecs0)" | grep -o "seed: .*" | cut -d' ' -f2)
launch ecs1 E2EB 7788
waitlog ecs1 "room world seed: $SEED" 120 && ok "B adopted A's seed ($SEED)" || fail "B adopted A's seed"
waitlog ecs1 "Forcing map seed '$SEED'" 120 && ok "B forced seed at level load" || fail "B forced seed at level load"

echo "[2/8] avatars both directions"
waitlog ecs0 "avatar spawned" 240 && ok "A spawned avatar for B" || fail "A spawned avatar for B"
waitlog ecs1 "avatar spawned" 240 && ok "B spawned avatar for A" || fail "B spawned avatar for A"
AUID=$(player_uid ecs0); BUID=$(player_uid ecs1)
[ -n "$AUID" ] && [ -n "$BUID" ] && ok "player uids resolved (A=$AUID B=$BUID)" || fail "player uids resolved"

echo "[3/8] teleport follow"
# Teleport A to B's player position — guaranteed walkable in the shared
# world (fixed coordinates land inside walls on some seeds).
BPPOS=$(cmd ecs1 state | grep "player:" | grep -o 'pos=([^)]*)' | tr -d 'pos=()')
TX=$(echo "$BPPOS" | cut -d, -f1); TY=$(echo "$BPPOS" | cut -d, -f2)
cmd ecs0 "tp $AUID $TX $TY" >/dev/null; sleep 6
BVIEW=$(cmd ecs1 ecs | grep "E2EA" | head -1)
echo "$BVIEW" | grep -q "pos=(${TX%%.*}" && ok "A's entity followed to B's position (~$TX,$TY)" || fail "A's entity followed to (~$TX,$TY): $BVIEW"

echo "[4/8] health sync"
cmd ecs0 "hp $AUID -25" >/dev/null; sleep 4
cmd ecs1 ecs | grep "E2EA" | grep -q "hp=75" && ok "A's hp 75 visible on B" || fail "A's hp 75 visible on B"

echo "[5/8] door sync (position addressed)"
DOOR=$(cmd ecs0 doors | grep "door uid=" | head -1)
DUID=$(echo "$DOOR" | grep -o 'uid=[0-9]*' | cut -d= -f2)
cmd ecs0 "opendoor $DUID $AUID" >/dev/null
waitlog ecs1 "opened by peer" 30 && ok "B opened the same door" || fail "B opened the same door"

echo "[6/8] ground item round trip"
cmd ecs0 "give $AUID Banana 2" >/dev/null
cmd ecs0 "drop $AUID Banana" >/dev/null
waitlog ecs1 "ground item 'Banana' dropped" 30 && ok "banana appeared on B" || fail "banana appeared on B"
BPOS=$(cmd ecs1 items | grep "item 'Banana'" | head -1 | grep -o 'pos=([^)]*)' | tr -d 'pos=()')
BX=$(echo "$BPOS" | cut -d, -f1); BY=$(echo "$BPOS" | cut -d, -f2)
cmd ecs1 "tp $BUID $BX $BY" >/dev/null; sleep 3
cmd ecs1 "pickup $BUID $BX $BY Banana" >/dev/null
waitlog ecs0 "taken by peer" 30 && ok "banana picked up by B, consumed on A" || fail "banana pickup propagated"

echo "[7/8] NPC sync: convergence + death"
sleep 10
NPC=$(cmd ecs0 npcs | grep "npc\[" | grep "dead=False" | grep -v "entity=-1" | head -1)
NUID=$(echo "$NPC" | grep -o 'uid=[0-9]*' | cut -d= -f2)
NIDX=$(echo "$NPC" | grep -o 'npc\[[0-9]*\]' | tr -d 'npc[]')
cmd ecs0 "kill $NUID" >/dev/null
waitlog ecs0 "npc death published" 30 && ok "A published npc[$NIDX] death" || fail "A published npc death"
sleep 5
cmd ecs1 npcs | grep "npc\[$NIDX\]" | grep -q "dead=True" && ok "B's npc[$NIDX] twin died" || fail "B's npc twin died"

echo "[8/8] travel together"
AVATARS_BEFORE=$(grep -ac "avatar spawned" "$(log ecs1)" || true)
cmd ecs0 nextlevel >/dev/null
waitlog ecs1 "following room: level 1 -> 2" 60 && ok "B followed to level 2" || fail "B followed to level 2"
RESPAWNED=1
for _ in $(seq 1 240); do
  [ "$(grep -ac 'avatar spawned' "$(log ecs1)" || true)" -gt "$AVATARS_BEFORE" ] && RESPAWNED=0 && break
  sleep 1
done
[ "$RESPAWNED" -eq 0 ] && ok "avatars respawned on level 2" || fail "avatars respawned on level 2"

echo "[9/9] player-vs-player: hit on avatar lands on the real player"
sleep 5
AVUID=$(cmd ecs0 agents | grep "'E2EB'" | grep -o 'uid=[0-9]*' | head -1 | cut -d= -f2)
if [ -z "$AVUID" ]; then
  fail "B's avatar found on A"
else
  ok "B's avatar found on A (uid $AVUID)"
  BHP_BEFORE=$(cmd ecs1 state | grep "player:" | grep -o 'hp=[0-9.]*' | head -1 | cut -d= -f2)
  cmd ecs0 "hp $AVUID -10" >/dev/null
  sleep 6
  BHP_AFTER=$(cmd ecs1 state | grep "player:" | grep -o 'hp=[0-9.]*' | head -1 | cut -d= -f2)
  check "[ \"\$(echo \"$BHP_BEFORE > $BHP_AFTER\" | bc)\" = 1 ]" "B's real player took the hit ($BHP_BEFORE -> $BHP_AFTER)"
  cmd ecs0 ecs | grep "E2EB" | grep -q "hp=$BHP_AFTER" && ok "B's new hp visible on A" || fail "B's new hp visible on A"
fi

echo
echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
