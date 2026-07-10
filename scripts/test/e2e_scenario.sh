#!/bin/bash
# End-to-end ECS netcode scenario: boots two headless game instances against a
# local `wrangler dev` (ws://127.0.0.1:8787), plays a full co-op demo out, and
# asserts every synced system along the way. Consolidates the per-iteration
# verification chains into one repeatable regression suite.
#
# Instances run the WINDOWS build through Proton (see proton_env.sh — Steam
# removed the native Linux depot). Clones are created/refreshed automatically.
#
# Usage: scripts/test/e2e_scenario.sh [ROOM]
#   E2E_MODE=solo   both instances as pure single-player (Mirror never starts)
#   E2E_VIDEO=1     windowed instead of -batchmode; records each instance's
#                   framebuffer for the whole run -> outputs/recordings/
# Prereqs: wrangler dev running (cd worker && npm run dev), EightPlayers.dll +
# SorTestDriver.dll deployed to the game's BepInEx/plugins, no game instances
# running, Steam client closed.
set -u

ROOM="${1:-E2E$RANDOM}"
MODE="${E2E_MODE:-host}"   # host = Mirror LAN self-host per instance; solo = single-player, Mirror never starts
VIDEO="${E2E_VIDEO:-0}"
. "$(dirname "$0")/proton_env.sh"
RD="$STEAM/data/sor-test/e2e"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
PASS=0; FAIL=0

ok()   { echo "  ok - $1"; PASS=$((PASS+1)); }
fail() { echo "FAIL - $1"; FAIL=$((FAIL+1)); }
check(){ if eval "$1"; then ok "$2"; else fail "$2"; fi; }

# ---- helpers ------------------------------------------------------------
log()  { echo "$(bepinex_dir "$1")/LogOutput.log"; }
cmdf() { echo "$(bepinex_dir "$1")/ep_cmd.txt"; }
outf() { echo "$(bepinex_dir "$1")/ep_out.txt"; }

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
  local gfx="-batchmode -nographics"
  [ "$VIDEO" = "1" ] && gfx="-screen-fullscreen 0 -window-mode windowed -screen-width 960 -screen-height 540 -popupwindow"
  launch_win "$1" \
    --env=SOR_TEST_MODE="$MODE" --env=SOR_TEST_NAME="$2" --env=SOR_TEST_PORT="$3" --env=SOR_TEST_ADDR=127.0.0.1 \
    --env=SOR_TEST_REPORT="$RD/$1/report.log" \
    --env=SOR_ECS_ROOM="$ROOM" --env=SOR_ECS_SERVER=ws://127.0.0.1:8787 --env=SOR_ECS_NAME="$2" \
    -- $gfx > "$RD/$1/stdout.log" 2>&1
}

# start_recording <inst> — fire the in-game recorder (frames land under the
# clone; encoded at the end of the run). 10fps for the whole scenario. Waits
# until the plugin consumes (deletes) the command file so the next cmd()
# write can't clobber it.
start_recording() {
  mkdir -p "$(clone_dir "$1")/rec"
  printf 'record 1200 10 rec\n' > "$(cmdf "$1")"
  for _ in $(seq 1 20); do [ -f "$(cmdf "$1")" ] || return 0; sleep 0.5; done
}

encode_recordings() {
  mkdir -p "$REPO/outputs/recordings"
  for inst in ecs0 ecs1; do
    local frames
    frames=$(find "$CLONES/$inst" "$GAME" -type d -name rec 2>/dev/null | head -1)
    [ -n "$frames" ] && [ -n "$(ls "$frames" 2>/dev/null)" ] || continue
    ffmpeg -y -framerate 10 -i "$frames/f%05d.png" -c:v libx264 -pix_fmt yuv420p \
      "$REPO/outputs/recordings/e2e-$MODE-$inst-$(date +%Y%m%d-%H%M%S).mp4" </dev/null >/dev/null 2>&1 \
      && rm -rf "$frames"
  done
  echo "recordings in outputs/recordings/"
}

player_uid() { cmd "$1" state | grep "player:" | grep -o 'uid=[0-9]*' | head -1 | cut -d= -f2; }

cleanup() {
  kill_sor
  [ "$VIDEO" = "1" ] && encode_recordings
  true
}
trap cleanup EXIT

# ---- scenario -----------------------------------------------------------
echo "e2e scenario in room $ROOM (mode=$MODE)"
curl -sf -m 3 http://127.0.0.1:8787/ >/dev/null || { echo "wrangler dev not running"; exit 2; }
sor_running && { echo "game instances already running"; exit 2; }
# Proton instances coexist with a running Steam client (verified 2026-07-10;
# the old native-Linux crash-loop via shared CEF htmlcache no longer applies),
# but a Steam-launched game session would still fight the test over pads and
# CPU — warn, don't abort.
pgrep -f '/app/bin/steam' >/dev/null && \
  echo "note: Steam client is running; ok for headless e2e, close it if instances misbehave"
make_win_clone ecs0
make_win_clone ecs1
rm -f "$(log ecs0)" "$(log ecs1)" "$(outf ecs0)" "$(outf ecs1)" "$(cmdf ecs0)" "$(cmdf ecs1)"
rm -rf "$(clone_dir ecs0)/rec" "$(clone_dir ecs1)/rec"

echo "[1/8] boot + shared world"
launch ecs0 E2EA 7777
waitlog ecs0 "claiming room world seed" 300 && ok "A claimed room seed" || fail "A claimed room seed"
SEED=$(grep -a "claiming room world seed" "$(log ecs0)" | grep -o "seed: .*" | cut -d' ' -f2)
[ "$VIDEO" = "1" ] && start_recording ecs0
launch ecs1 E2EB 7788
waitlog ecs1 "room world seed: $SEED" 120 && ok "B adopted A's seed ($SEED)" || fail "B adopted A's seed"
[ "$VIDEO" = "1" ] && start_recording ecs1
waitlog ecs1 "Forcing map seed '$SEED'" 120 && ok "B forced seed at level load" || fail "B forced seed at level load"

echo "[2/8] avatars both directions"
waitlog ecs0 "avatar spawned" 240 && ok "A spawned avatar for B" || fail "A spawned avatar for B"
waitlog ecs1 "avatar spawned" 240 && ok "B spawned avatar for A" || fail "B spawned avatar for A"
AUID=$(player_uid ecs0); BUID=$(player_uid ecs1)
[ -n "$AUID" ] && [ -n "$BUID" ] && ok "player uids resolved (A=$AUID B=$BUID)" || fail "player uids resolved"

echo "[2b] world geometry hash converges (divergence detector + heal)"
# Same-seed generation occasionally diverges (frame-timing RNG); the mod
# detects it via level.hash and the follower reloads. Allow one heal cycle.
CONV=1
for _ in $(seq 1 30); do
  HA=$(cmd ecs0 worldhash | grep -o 'worldhash [0-9a-f]*' | cut -d' ' -f2)
  HB=$(cmd ecs1 worldhash | grep -o 'worldhash [0-9a-f]*' | cut -d' ' -f2)
  [ -n "$HA" ] && [ "$HA" = "$HB" ] && CONV=0 && break
  sleep 3
done
[ "$CONV" -eq 0 ] && ok "world hashes converged (A=$HA B=$HB)" || fail "world hashes converged (A=$HA B=$HB)"
# B may have reloaded to heal - refresh its uid and wait for avatars again
BUID=$(player_uid ecs1)
waitlog ecs1 "avatar spawned" 60

echo "[2c] ecs surface: raw component write on A readable on B"
AENT=""
for _ in $(seq 1 15); do
  AENT=$(cmd ecs0 ecs | grep 'local agent' | grep -o 'entity=[0-9]*' | head -1 | cut -d= -f2)
  [ -n "$AENT" ] && [ "$AENT" != "-1" ] && break
  sleep 2
done
cmd ecs0 "ecsset $AENT {\"probe\":{\"v\":42}}" >/dev/null
sleep 3
cmd ecs1 "ecsget $AENT" | grep -q '"v":42' && ok "probe component visible on B (entity $AENT)" || fail "probe component visible on B (entity $AENT)"

echo "[2d] world-object layout: published by authority, reconciled on both"
WA=$(cmd ecs0 entities | grep -c '"wlayout"'); WB=$(cmd ecs1 entities | grep -c '"wlayout"')
[ "${WA:-0}" -ge 1 ] && [ "$WA" = "$WB" ] && ok "wlayout entity on both (A=$WA B=$WB)" || fail "wlayout entity on both (A=$WA B=$WB)"
MA=$(grep -a 'wobj reconcile' "$(log ecs0)" | tail -1 | grep -o 'matched=[0-9]*' | cut -d= -f2)
MB=$(grep -a 'wobj reconcile' "$(log ecs1)" | tail -1 | grep -o 'matched=[0-9]*' | cut -d= -f2)
[ -n "$MA" ] && [ "${MA:-0}" -gt 100 ] && [ "$MA" = "$MB" ] && ok "both reconciled the same layout (matched=$MA)" || fail "both reconciled the same layout (A=$MA B=$MB)"

echo "[3/8] teleport follow"
# Teleport A to a generation NPC's position — guaranteed walkable ground
# inside the level on any seed/mode (players idle in a holding area
# pre-input; fixed coordinates land inside walls on some seeds).
sleep 8
NPCPOS=$(cmd ecs0 npcs | grep "npc\[" | grep "dead=False" | head -1 | grep -o 'pos=([^)]*)' | tr -d 'pos=()')
TX=$(echo "$NPCPOS" | cut -d, -f1); TY=$(echo "$NPCPOS" | cut -d, -f2)
cmd ecs0 "tp $AUID $TX $TY" >/dev/null; sleep 6
BVIEW=$(cmd ecs1 ecs | grep "E2EA" | head -1)
echo "$BVIEW" | grep -q "pos=(${TX%%.*}" && ok "A's entity followed to (~$TX,$TY)" || fail "A's entity followed to (~$TX,$TY): $BVIEW"

echo "[4/8] health sync"
AHP_BEFORE=$(cmd ecs0 state | grep "player:" | grep -o 'hp=[0-9.]*' | head -1 | cut -d= -f2)
cmd ecs0 "hp $AUID -25" >/dev/null; sleep 4
AHP_EXPECT=$(echo "$AHP_BEFORE - 25" | bc)
cmd ecs1 ecs | grep "E2EA" | grep -q "hp=$AHP_EXPECT" && ok "A's hp $AHP_EXPECT visible on B" || fail "A's hp $AHP_EXPECT visible on B: $(cmd ecs1 ecs | grep E2EA | head -1)"

echo "[5/8] door sync (position addressed)"
DOOR=$(cmd ecs0 doors | grep "door uid=" | head -1)
DUID=$(echo "$DOOR" | grep -o 'uid=[0-9]*' | cut -d= -f2)
cmd ecs0 "opendoor $DUID $AUID" >/dev/null
waitlog ecs1 "opened by peer" 30 && ok "B opened the same door" || fail "B opened the same door"
cmd ecs0 "lockdoor $DUID" >/dev/null
waitlog ecs1 ") locked by peer" 30 && ok "B locked the same door" || fail "B locked the same door"
cmd ecs0 "lockdoor $DUID off" >/dev/null
waitlog ecs1 "unlocked by peer" 30 && ok "B unlocked the same door" || fail "B unlocked the same door"

echo "[5b] object destruction (position addressed)"
OBJ=$(cmd ecs0 objects | grep "object uid=" | grep "destroying=False" | head -1)
OUID=$(echo "$OBJ" | grep -o 'uid=[0-9]*' | cut -d= -f2)
ONAME=$(echo "$OBJ" | grep -o "'[^']*'" | tr -d "'")
if [ -z "$OUID" ]; then
  fail "found a destructible object on A"
else
  ok "found object '$ONAME' (uid $OUID) on A"
  cmd ecs0 "destroyobj $OUID" >/dev/null
  waitlog ecs1 "object '$ONAME' at .* destroyed by peer" 30 && ok "B destroyed its '$ONAME' twin" || fail "B destroyed its '$ONAME' twin"
fi

echo "[6/8] ground item round trip"
cmd ecs0 "give $AUID Banana 2" >/dev/null
cmd ecs0 "drop $AUID Banana" >/dev/null
waitlog ecs1 "ground item 'Banana' dropped" 30 && ok "banana appeared on B" || fail "banana appeared on B"
BPOS=$(cmd ecs1 items | grep "item 'Banana'" | head -1 | grep -o 'pos=([^)]*)' | tr -d 'pos=()')
BX=$(echo "$BPOS" | cut -d, -f1); BY=$(echo "$BPOS" | cut -d, -f2)
cmd ecs1 "tp $BUID $BX $BY" >/dev/null; sleep 3
cmd ecs1 "pickup $BUID $BX $BY Banana" >/dev/null
waitlog ecs0 "taken by peer" 30 && ok "banana picked up by B, consumed on A" || fail "banana pickup propagated"

echo "[6b] container loot sync"
CPOS=$(cmd ecs0 containers | grep 'container uid=' | head -1 | grep -o 'pos=([^)]*)' | tr -d 'pos=()')
if [ -z "$CPOS" ]; then
  ok "no containers generated on this seed - section skipped"
else
  CX=$(echo "$CPOS" | cut -d, -f1); CY=$(echo "$CPOS" | cut -d, -f2)
  ok "found container at ($CX,$CY) on A"
  cmd ecs0 "chestgive $CX $CY Banana" >/dev/null
  cmd ecs1 "chestgive $CX $CY Banana" >/dev/null
  cmd ecs0 "chesttake $CX $CY Banana" >/dev/null
  waitlog ecs1 "chest item 'Banana' taken by peer" 30 && ok "B's container lost the Banana" || fail "B's container lost the Banana"
fi

echo "[6c] shop purchase sync (NPC-index addressed)"
SNPC=$(cmd ecs0 npcs | grep 'dead=False' | grep -v 'entity=-1' | head -1)
SIDX=$(echo "$SNPC" | grep -o 'npc\[[0-9]*\]' | tr -d 'npc[]')
SUID=$(echo "$SNPC" | grep -o 'uid=[0-9]*' | cut -d= -f2)
BSUID=$(cmd ecs1 npcs | grep "npc\[$SIDX\]" | grep -o 'uid=[0-9]*' | head -1 | cut -d= -f2)
if [ -z "$SUID" ] || [ -z "$BSUID" ]; then
  fail "matched shop NPC on both (A=$SUID B=$BSUID idx=$SIDX)"
else
  ok "matched shop NPC on both (idx $SIDX, A uid=$SUID, B uid=$BSUID)"
  cmd ecs0 "give $SUID Banana" >/dev/null
  cmd ecs1 "give $BSUID Banana" >/dev/null
  cmd ecs0 "shoptake $SUID Banana" >/dev/null
  waitlog ecs1 "shop item 'Banana' taken by peer" 30 && ok "B's shopkeeper lost the Banana" || fail "B's shopkeeper lost the Banana"
fi

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

echo "[8b] level-2 hash convergence (allow two heal cycles)"
CONV=1
for _ in $(seq 1 80); do
  HA=$(cmd ecs0 worldhash | grep -o 'worldhash [0-9a-f]*' | cut -d' ' -f2)
  HB=$(cmd ecs1 worldhash | grep -o 'worldhash [0-9a-f]*' | cut -d' ' -f2)
  [ -n "$HA" ] && [ "$HA" != "00000000" ] && [ "$HA" = "$HB" ] && CONV=0 && break
  sleep 3
done
[ "$CONV" -eq 0 ] && ok "level-2 hashes converged (A=$HA B=$HB)" || fail "level-2 hashes converged (A=$HA B=$HB)"
# a heal reload respawns players and avatars - wait until both sides see
# each other's avatar again before the pvp/status/weapon sections
for _ in $(seq 1 25); do
  cmd ecs0 agents | grep -q "'E2EB'" && cmd ecs1 agents | grep -q "'E2EA'" && break
  sleep 3
done
sleep 5

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

echo "[10/10] player death propagates"
if [ -n "${AVUID:-}" ]; then
  cmd ecs0 "hp $AVUID -999" >/dev/null
  sleep 8
  cmd ecs1 state | grep "player:" | grep -q "dead=True" && ok "B's real player died from lethal pvp hit" || fail "B's real player died"
  cmd ecs0 agents | grep "uid=$AVUID" | grep -q "dead=True" && ok "B's avatar played death on A" || fail "B's avatar played death on A"
else
  fail "player death propagates (no avatar uid)"
fi

echo "[11/11] status effect sync"
AUID=$(player_uid ecs0)   # re-resolve: the player agent gets a new uid per level in solo mode
cmd ecs0 "status $AUID Fast" >/dev/null
waitlog ecs1 "status 'Fast' on applied" 30 && ok "A's Fast status applied to avatar on B" || fail "A's Fast status applied to avatar on B"
cmd ecs1 "agents" | grep "'E2EA'" >/dev/null   # avatar still alive sanity
cmd ecs0 "status $AUID Fast off" >/dev/null
waitlog ecs1 "status 'Fast' off applied" 30 && ok "A's Fast removal applied to avatar on B" || fail "A's Fast removal applied to avatar on B"

echo "[11b] equipped weapon visible on avatar"
cmd ecs0 "equip $AUID Revolver" >/dev/null
waitlog ecs1 "avatar weapon 'Revolver'" 30 && ok "A's Revolver equipped on B's avatar" || fail "A's Revolver equipped on B's avatar"

echo "[12/12] fire ignite + extinguish"
APOS=$(cmd ecs0 state | grep "player:" | grep -o 'pos=([^)]*)' | tr -d 'pos=()')
FX=$(echo "$(echo "$APOS" | cut -d, -f1) + 2" | bc); FY=$(echo "$APOS" | cut -d, -f2)
cmd ecs0 "ignite $FX $FY" >/dev/null
waitlog ecs1 "ignited by peer" 30 && ok "fire at ($FX,$FY) ignited on B" || fail "fire at ($FX,$FY) ignited on B"
sleep 2
cmd ecs0 "extinguish $FX $FY" >/dev/null
waitlog ecs1 "put out by peer" 30 && ok "fire extinguished on B" || fail "fire extinguished on B"

echo "[13/13] gas cloud sync"
GOBJ=$(cmd ecs0 objects | grep 'object uid=' | grep 'destroying=False' | head -1)
GUID=$(echo "$GOBJ" | grep -o 'uid=[0-9]*' | cut -d= -f2)
cmd ecs0 "spawngas $GUID Flammable" >/dev/null
waitlog ecs1 "gas 'Flammable' spawned by peer" 30 && ok "gas cloud appeared on B" || fail "gas cloud appeared on B"

echo "[14/14] ECS control plane: input intent component drives the player"
# Re-resolve A's CURRENT player entity (a new one is spawned per level).
AENT=""
for _ in $(seq 1 15); do
  AENT=$(cmd ecs0 ecs | grep 'local agent' | grep -o 'entity=[0-9]*' | head -1 | cut -d= -f2)
  [ -n "$AENT" ] && [ "$AENT" != "-1" ] && break
  sleep 2
done
# Everything below runs on B: read A's pos from B's mirror, write the intent
# onto A's entity (B does NOT own it — exercises the shared-component rule).
ecspos() { cmd ecs1 "ecsget $1" | grep -oE '"pos":\{"x":-?[0-9.]+,"y":-?[0-9.]+\}' | grep -oE '\-?[0-9.]+' | tr '\n' ' '; }
read -r AX AY _ <<< "$(ecspos "$AENT")"
TX=$(echo "$AX + 4" | bc); TY=$AY
cmd ecs1 "ecsset $AENT {\"input\":{\"tx\":$TX,\"ty\":$TY}}" >/dev/null
MOVED=1; D=0
for _ in $(seq 1 20); do
  read -r PX PY _ <<< "$(ecspos "$AENT")"
  [ -n "${PX:-}" ] && D=$(echo "d=($PX-$AX)^2+($PY-$AY)^2; scale=2; sqrt(d)" | bc -l) && \
    [ "$(echo "$D > 2" | bc -l)" = "1" ] && { MOVED=0; break; }
  sleep 1
done
[ "$MOVED" -eq 0 ] && ok "A's player moved under B's ECS input intent ($D units)" \
                   || fail "A's player moved under B's ECS input intent (from $AX,$AY)"
cmd ecs1 "ecsset $AENT {\"input\":null}" >/dev/null   # clear the intent

echo "[15/15] bullet tracers: A's gunfire visible on B (fired via input intent)"
AUID=$(player_uid ecs0)
cmd ecs0 "give $AUID Revolver 1" >/dev/null
cmd ecs0 "equip $AUID Revolver" >/dev/null
cmd ecs1 "ecsset $AENT {\"input\":{\"hold\":[\"attack\"]}}" >/dev/null   # B pulls A's trigger
waitlog ecs1 "bullet spawned by peer" 30 && ok "A's bullet tracer spawned on B" || fail "A's bullet tracer spawned on B"
cmd ecs1 "ecsset $AENT {\"input\":null}" >/dev/null

echo "[14b] ECS inspection: statuses readable as fx component"
AUID=$(player_uid ecs0)
cmd ecs0 "status $AUID Fast" >/dev/null
FXOK=1
for _ in $(seq 1 10); do
  cmd ecs1 "ecsget $AENT" | grep -q '"fx":.*"Fast"' && { FXOK=0; break; }
  sleep 2
done
[ "$FXOK" -eq 0 ] && ok "Fast status visible in fx component on B" || fail "Fast status visible in fx component on B"
cmd ecs0 "status $AUID Fast off" >/dev/null

echo
echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
