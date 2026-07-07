#!/usr/bin/env node
// Watch a sor-ecs-net room: joins as a spectator client and prints every
// event. Useful for confirming a live game instance is publishing entities.
// Usage: node scripts/test/ecs_room_watch.mjs [ROOM] [ws://localhost:8787] [seconds]

const room = (process.argv[2] ?? 'DEVTEST').toUpperCase()
const base = (process.argv[3] ?? 'ws://localhost:8787').replace(/\/$/, '')
const seconds = Number(process.argv[4] ?? 120)

const ws = new WebSocket(`${base}/room/${room}/ws`)
const stamp = () => new Date().toISOString().slice(11, 19)

ws.onopen = () => {
  ws.send(JSON.stringify({ t: 'hello', proto: 1, name: 'watcher' }))
  console.log(`${stamp()} watching room ${room} on ${base} for ${seconds}s`)
}
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data)
  if (m.t === 'set') {
    const p = m.components?.pos
    console.log(`${stamp()} set e=${m.e}${p ? ` pos=(${p.x.toFixed(1)}, ${p.y.toFixed(1)})` : ` ${JSON.stringify(m.components)}`}`)
  } else {
    console.log(`${stamp()} ${JSON.stringify(m)}`)
  }
}
ws.onclose = () => process.exit(0)
ws.onerror = (e) => {
  console.error('ws error:', e.message ?? e)
  process.exit(1)
}
setTimeout(() => { ws.close(); process.exit(0) }, seconds * 1000)
