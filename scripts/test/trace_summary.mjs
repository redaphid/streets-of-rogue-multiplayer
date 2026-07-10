#!/usr/bin/env node
// Summarize a SOR_TRACE JSONL behavior trace: event counts per cat/ev,
// time span, and a few sample lines per event type.
// Usage: node scripts/test/trace_summary.mjs <trace.jsonl> [--samples N]

import { createReadStream } from 'node:fs'
import { createInterface } from 'node:readline'

const file = process.argv[2]
if (!file) {
  console.error('usage: trace_summary.mjs <trace.jsonl> [--samples N]')
  process.exit(2)
}
const samplesWanted = process.argv.includes('--samples')
  ? Number(process.argv[process.argv.indexOf('--samples') + 1])
  : 2

const counts = new Map()
const samples = new Map()
let first = Infinity
let last = -Infinity
let total = 0
let badLines = 0

const rl = createInterface({ input: createReadStream(file) })
for await (const line of rl) {
  if (!line.trim()) continue
  let ev
  try {
    ev = JSON.parse(line)
  } catch {
    badLines++
    continue
  }
  total++
  const key = `${ev.cat}/${ev.ev}`
  counts.set(key, (counts.get(key) ?? 0) + 1)
  if (typeof ev.ts === 'number') {
    first = Math.min(first, ev.ts)
    last = Math.max(last, ev.ts)
  }
  const bucket = samples.get(key) ?? []
  if (bucket.length < samplesWanted) {
    bucket.push(line.length > 160 ? line.slice(0, 160) + '…' : line)
    samples.set(key, bucket)
  }
}

console.log(`${file}`)
console.log(`${total} events over ${(last - first).toFixed(1)}s (ts ${first}..${last})${badLines ? `, ${badLines} unparseable lines` : ''}`)
console.log()
for (const [key, n] of [...counts].sort((a, b) => b[1] - a[1])) {
  console.log(`${String(n).padStart(7)}  ${key}`)
  for (const s of samples.get(key) ?? []) console.log(`         ${s}`)
}
