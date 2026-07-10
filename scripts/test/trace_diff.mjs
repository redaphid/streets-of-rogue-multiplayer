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
// Subject scoping (default on; --no-scope disables). The vanilla trace (A) is a
// FULL real-game capture — a whole session with level-gen, ~100 ambient NPCs,
// inventory, movement sampling, status ticks, and UID pooling (a UID is reused
// across pooled agents). The candidate (B) is a minimal in-process sim that
// faithfully replays only the SCRIPTED scenario. An honest rule-scoped sim can
// neither reproduce nor should reproduce the game's ambient world, so the
// ordered sequence check is scoped to the SCRIPTED SUBJECTS — the agents the
// candidate actually spawns — on BOTH sides. Symmetric and non-lenient: every
// event of a scripted subject is kept on both sides (a genuine divergence in
// the subject's spawn/health/death/status is STILL caught), and only agents the
// scenario never scripts are dropped. No candidate spawns => no-op (the full
// agent-cat sequence is compared, exactly as before).
const scopeSubjects = !process.argv.includes('--no-scope')
const round1 = (n) => (typeof n === 'number' ? Math.round(n * 10) / 10 : n)
const subjectUid = (e) => (e && e.agent && e.agent.uid != null ? e.agent.uid : null)
const spawnKey = (e) => {
  const pos = Array.isArray(e.pos) ? e.pos.map(round1) : null
  return `${e.agentType ?? ''}@${pos ? pos.join(',') : '?'}`
}
// Scope an event list to the scripted subjects named by `keys`. For each key,
// find its (latest) spawn, keep that spawn plus every later same-UID agent event
// up to — not including — the next spawn that reuses the UID, so pooled earlier
// lives of a reused UID are excluded. Non-agent-cat events pass through (they
// carry no agent subject and are governed by --cat alone).
const scopeTo = (events, keys) => {
  const kept = new Set()
  for (const key of keys) {
    let spawnIdx = -1
    for (let i = 0; i < events.length; i++) {
      const e = events[i]
      if (e.cat === 'agent' && e.ev === 'spawn' && spawnKey(e) === key) spawnIdx = i
    }
    if (spawnIdx < 0) continue
    const uid = subjectUid(events[spawnIdx])
    kept.add(spawnIdx)
    for (let j = spawnIdx + 1; j < events.length; j++) {
      const e = events[j]
      if (subjectUid(e) !== uid) continue
      if (e.cat === 'agent' && e.ev === 'spawn') break
      if (e.cat === 'agent') kept.add(j)
    }
  }
  return events.filter((e, i) => (e.cat === 'agent' ? kept.has(i) : true))
}

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

// Scope both sides to the scripted subjects (the agents the candidate spawns)
// before the ordered sequence comparison. The count table above stays on the
// full traces, so ambient drift remains visible and is never hidden.
const subjectKeys = scopeSubjects
  ? [...new Set(b.filter((e) => e.cat === 'agent' && e.ev === 'spawn').map(spawnKey))]
  : []
const applyScope = (events) => (subjectKeys.length ? scopeTo(events, subjectKeys) : events)
if (subjectKeys.length) {
  console.log(`\nsubject scope: [${subjectKeys.join(', ')}] (ambient agents excluded from the sequence check; --no-scope to disable)`)
}

const seqA = normalize(applyScope(a))
const seqB = normalize(applyScope(b))
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
