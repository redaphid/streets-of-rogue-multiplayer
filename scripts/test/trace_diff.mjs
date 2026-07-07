#!/usr/bin/env node
// Diff two SOR_TRACE behavior traces for ECS-parity checking.
// Usage: node scripts/test/trace_diff.mjs <vanilla.jsonl> <candidate.jsonl> [--cat agent,inv,door]
//
// Traces are never byte-identical (timestamps, UIDs, RNG-driven NPC noise), so
// the comparison is structural:
//   1. event-count table per cat/ev with drift percentages
//   2. ordered sequence match on the chosen categories after normalization
//      (ts stripped, agent uids replaced by first-seen index, floats rounded)
// Exit code 1 when sequences diverge — usable as a test assertion.

import { readFileSync } from 'node:fs'

const [fileA, fileB] = [process.argv[2], process.argv[3]]
if (!fileA || !fileB) {
  console.error('usage: trace_diff.mjs <vanilla.jsonl> <candidate.jsonl> [--cat agent,inv]')
  process.exit(2)
}
const catArg = process.argv.includes('--cat')
  ? process.argv[process.argv.indexOf('--cat') + 1].split(',')
  : ['agent', 'inv', 'door', 'level']
const cats = new Set(catArg)

const load = (file) =>
  readFileSync(file, 'utf8')
    .split('\n')
    .filter((l) => l.trim())
    .flatMap((l) => {
      try {
        return [JSON.parse(l)]
      } catch {
        return []
      }
    })

const normalize = (events) => {
  const uidIndex = new Map()
  const uid = (u) => {
    if (u == null) return null
    if (!uidIndex.has(u)) uidIndex.set(u, `#${uidIndex.size}`)
    return uidIndex.get(u)
  }
  const walk = (v, key) => {
    if (v && typeof v === 'object') {
      const out = Array.isArray(v) ? [] : {}
      for (const [k, inner] of Object.entries(v)) {
        if (k === 'ts') continue
        out[k] = walk(inner, k)
      }
      return out
    }
    if (key === 'uid' || key === 'door') return uid(v)
    if (typeof v === 'number') return Math.round(v * 10) / 10
    return v
  }
  return events.filter((e) => cats.has(e.cat)).map((e) => JSON.stringify(walk(e)))
}

const a = load(fileA)
const b = load(fileB)

const countBy = (events) => {
  const m = new Map()
  for (const e of events) m.set(`${e.cat}/${e.ev}`, (m.get(`${e.cat}/${e.ev}`) ?? 0) + 1)
  return m
}
const ca = countBy(a)
const cb = countBy(b)

console.log('event counts (A=vanilla, B=candidate):')
for (const key of [...new Set([...ca.keys(), ...cb.keys()])].sort()) {
  const na = ca.get(key) ?? 0
  const nb = cb.get(key) ?? 0
  const drift = na === nb ? '' : na === 0 || nb === 0 ? '  ONLY ONE SIDE' : `  ${(((nb - na) / na) * 100).toFixed(0)}%`
  console.log(`  ${key.padEnd(18)} A=${String(na).padStart(6)}  B=${String(nb).padStart(6)}${drift}`)
}

const seqA = normalize(a)
const seqB = normalize(b)
let firstDiff = -1
for (let i = 0; i < Math.max(seqA.length, seqB.length); i++) {
  if (seqA[i] !== seqB[i]) {
    firstDiff = i
    break
  }
}

console.log(`\nsequence check on [${[...cats].join(', ')}]: A=${seqA.length} events, B=${seqB.length} events`)
if (firstDiff === -1) {
  console.log('sequences MATCH')
  process.exit(0)
}
console.log(`sequences DIVERGE at index ${firstDiff}:`)
console.log(`  A: ${seqA[firstDiff] ?? '(end)'}`)
console.log(`  B: ${seqB[firstDiff] ?? '(end)'}`)
process.exit(1)
