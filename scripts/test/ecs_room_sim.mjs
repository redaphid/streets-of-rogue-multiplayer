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

  alice.ws.close()
  carol.ws.close()
} catch (err) {
  console.error('FAIL -', err.message)
  failures++
}

console.log(failures === 0 ? '\nALL PASS' : `\n${failures} FAILURE(S)`)
process.exit(failures === 0 ? 0 : 1)
