#!/usr/bin/env node
// Simulates two game clients against the sor-ecs-net worker (wrangler dev or deployed).
// Usage: node scripts/test/ecs_room_sim.mjs [ws://localhost:8787]
// Exercises: hello/welcome, spawn echo + broadcast, snapshot for late joiner,
// position set broadcast, ownership rejection, despawn-on-disconnect.

const base = (process.argv[2] ?? 'ws://localhost:8787').replace(/\/$/, '')
const room = `TEST-${process.pid % 10000}`
const url = `${base}/room/${room}/ws`

let failures = 0
const ok = (cond, label) => {
  console.log(`${cond ? '  ok' : 'FAIL'} - ${label}`)
  if (!cond) failures++
}

const connect = (name) =>
  new Promise((resolve, reject) => {
    const ws = new WebSocket(url)
    const client = { ws, name, inbox: [], waiters: [] }
    ws.onmessage = (ev) => {
      const msg = JSON.parse(ev.data)
      const w = client.waiters.findIndex((f) => f.pred(msg))
      if (w >= 0) client.waiters.splice(w, 1)[0].resolve(msg)
      else client.inbox.push(msg)
    }
    ws.onopen = () => {
      ws.send(JSON.stringify({ t: 'hello', proto: 1, name }))
      resolve(client)
    }
    ws.onerror = (e) => reject(new Error(`ws error for ${name}: ${e.message ?? e}`))
  })

const recv = (client, pred, label, ms = 5000) => {
  const i = client.inbox.findIndex(pred)
  if (i >= 0) return Promise.resolve(client.inbox.splice(i, 1)[0])
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`timeout waiting for: ${label}`)), ms)
    client.waiters.push({ pred, resolve: (m) => { clearTimeout(timer); resolve(m) } })
  })
}

try {
  // --- alice joins an empty room ---
  const alice = await connect('alice')
  const aliceWelcome = await recv(alice, (m) => m.t === 'welcome', 'alice welcome')
  ok(aliceWelcome.snapshot.length === 0, 'alice sees empty snapshot in fresh room')
  ok(aliceWelcome.peers.length === 0, 'alice sees no peers')

  // --- alice spawns her player entity ---
  alice.ws.send(JSON.stringify({ t: 'spawn', tmp: 42, components: { player: { name: 'alice', color: 1 }, pos: { x: 1, y: 2 } } }))
  const aliceSpawn = await recv(alice, (m) => m.t === 'spawn', 'alice spawn echo')
  ok(aliceSpawn.tmp === 42, 'spawn echo carries tmp id')
  ok(aliceSpawn.owner === aliceWelcome.you, 'spawned entity owned by alice')
  const aliceEntity = aliceSpawn.e

  // --- bob joins late, must get alice in snapshot ---
  const bob = await connect('bob')
  const bobWelcome = await recv(bob, (m) => m.t === 'welcome', 'bob welcome')
  ok(bobWelcome.snapshot.length === 1, 'bob snapshot contains 1 entity')
  ok(bobWelcome.snapshot[0]?.e === aliceEntity, 'bob snapshot has alice entity')
  ok(bobWelcome.snapshot[0]?.components.player?.name === 'alice', 'snapshot preserves components')
  ok(bobWelcome.peers.some((p) => p.name === 'alice'), 'bob sees alice as peer')
  const bobJoined = await recv(alice, (m) => m.t === 'peer' && m.joined, 'alice notified of bob')
  ok(bobJoined.name === 'bob', 'peer-joined carries name')

  // --- alice moves; bob receives the set ---
  alice.ws.send(JSON.stringify({ t: 'set', e: aliceEntity, components: { pos: { x: 9, y: 8 } } }))
  const move = await recv(bob, (m) => m.t === 'set' && m.e === aliceEntity, 'bob receives move')
  ok(move.components.pos.x === 9, 'position update relayed')

  // --- bob may not write alice's entity ---
  bob.ws.send(JSON.stringify({ t: 'set', e: aliceEntity, components: { pos: { x: 0, y: 0 } } }))
  const rejected = await recv(bob, (m) => m.t === 'error', 'ownership rejection')
  ok(/rejected/.test(rejected.message), 'foreign set rejected')

  // --- bob spawns, then disconnects; alice sees spawn, despawn, peer-left ---
  bob.ws.send(JSON.stringify({ t: 'spawn', tmp: 7, components: { player: { name: 'bob', color: 2 } } }))
  const bobSpawnAtAlice = await recv(alice, (m) => m.t === 'spawn', 'alice sees bob spawn')
  bob.ws.close()
  const despawn = await recv(alice, (m) => m.t === 'despawn', 'alice sees despawn on disconnect')
  ok(despawn.e === bobSpawnAtAlice.e, 'despawned entity is bobs')
  const left = await recv(alice, (m) => m.t === 'peer' && !m.joined, 'alice sees peer-left')
  ok(left.name === 'bob', 'peer-left carries name')

  // --- late joiner after all that sees only alice's entity ---
  const carol = await connect('carol')
  const carolWelcome = await recv(carol, (m) => m.t === 'welcome', 'carol welcome')
  ok(carolWelcome.snapshot.length === 1, 'carol snapshot back to 1 entity')
  ok(carolWelcome.world === null, 'world seed unset before anyone claims it')

  // --- world seed: first write wins, broadcast, appears in welcome ---
  alice.ws.send(JSON.stringify({ t: 'world', seed: 'seed-alpha' }))
  const worldAtCarol = await recv(carol, (m) => m.t === 'world', 'carol receives world seed')
  ok(worldAtCarol.seed === 'seed-alpha', 'world seed broadcast to peers')
  carol.ws.send(JSON.stringify({ t: 'world', seed: 'seed-beta' }))
  const reminded = await recv(carol, (m) => m.t === 'world', 'carol reminded of existing seed')
  ok(reminded.seed === 'seed-alpha', 'second world write loses (first wins)')
  const dave = await connect('dave')
  const daveWelcome = await recv(dave, (m) => m.t === 'welcome', 'dave welcome')
  ok(daveWelcome.world?.seed === 'seed-alpha', 'late joiner gets world seed in welcome')
  ok(daveWelcome.world?.num === 1, 'world starts at level 1')

  // --- level number: monotonic bump, broadcast, stale ignored ---
  alice.ws.send(JSON.stringify({ t: 'world', num: 2 }))
  const bump = await recv(carol, (m) => m.t === 'world' && m.num === 2, 'carol sees level bump')
  ok(bump.seed === 'seed-alpha', 'level bump carries seed')
  carol.ws.send(JSON.stringify({ t: 'world', num: 1 }))
  const stale = await recv(carol, (m) => m.t === 'world', 'carol answered on stale bump')
  ok(stale.num === 2, 'stale level proposal ignored (num stays 2)')
  const erin = await connect('erin')
  const erinWelcome = await recv(erin, (m) => m.t === 'welcome', 'erin welcome')
  ok(erinWelcome.world?.num === 2, 'late joiner gets current level num')
  erin.ws.close()
  dave.ws.close()

  // --- transient events: relayed to peers with sender id, not echoed back ---
  alice.ws.send(JSON.stringify({ t: 'event', kind: 'door-open', data: { door: 123 } }))
  const evt = await recv(carol, (m) => m.t === 'event', 'carol receives event')
  ok(evt.kind === 'door-open' && evt.data?.door === 123, 'event kind+data relayed')
  ok(evt.from === 1, 'event carries sender id')

  alice.ws.close()
  carol.ws.close()
} catch (err) {
  console.error('FAIL -', err.message)
  failures++
}

console.log(failures === 0 ? '\nALL PASS' : `\n${failures} FAILURE(S)`)
process.exit(failures === 0 ? 0 : 1)
