import { describe, expect, it } from 'vitest'
import { RoomWorld } from './ecs'
import { runSystems } from './systems'

describe('damage system (pvp-hit)', () => {
  function worldWithPlayer(hp = 80, max = 80) {
    const w = new RoomWorld()
    const e = w.spawn(1, { player: { name: 'a' }, hp: { cur: hp, max } })
    return { w, e }
  }

  it('applies damage authoritatively to the entity hp component', () => {
    const { w, e } = worldWithPlayer(80)
    const out = runSystems(w, { from: 2, kind: 'pvp-hit', data: { e, dmg: 30 } })
    expect(w.get(e)?.components.hp).toEqual({ cur: 50, max: 80 })
    expect(out.sets).toEqual([{ e, components: { hp: { cur: 50, max: 80 } } }])
  })

  it('clamps at zero and marks the entity dead', () => {
    const { w, e } = worldWithPlayer(20)
    const out = runSystems(w, { from: 2, kind: 'pvp-hit', data: { e, dmg: 55 } })
    expect(w.get(e)?.components.hp).toEqual({ cur: 0, max: 80 })
    expect(w.get(e)?.components.dead).toEqual({ v: true })
    expect(out.sets).toEqual([
      { e, components: { hp: { cur: 0, max: 80 }, dead: { v: true } } },
    ])
  })

  it('ignores hits on missing entities or entities without hp', () => {
    const w = new RoomWorld()
    const bare = w.spawn(1, { player: { name: 'x' } })
    expect(runSystems(w, { from: 2, kind: 'pvp-hit', data: { e: 999, dmg: 5 } }).sets).toEqual([])
    expect(runSystems(w, { from: 2, kind: 'pvp-hit', data: { e: bare, dmg: 5 } }).sets).toEqual([])
  })

  it('ignores nonsense damage (negative, NaN, missing)', () => {
    const { w, e } = worldWithPlayer(80)
    expect(runSystems(w, { from: 2, kind: 'pvp-hit', data: { e, dmg: -10 } }).sets).toEqual([])
    expect(runSystems(w, { from: 2, kind: 'pvp-hit', data: { e } }).sets).toEqual([])
    expect(w.get(e)?.components.hp).toEqual({ cur: 80, max: 80 })
  })

  it('does not resurrect: hits on a dead entity are ignored', () => {
    const { w, e } = worldWithPlayer(10)
    runSystems(w, { from: 2, kind: 'pvp-hit', data: { e, dmg: 50 } })
    const out = runSystems(w, { from: 2, kind: 'pvp-hit', data: { e, dmg: 50 } })
    expect(out.sets).toEqual([])
  })

  it('passes unhandled event kinds through untouched', () => {
    const { w } = worldWithPlayer()
    const out = runSystems(w, { from: 1, kind: 'door-open', data: { x: 1, y: 2 } })
    expect(out.sets).toEqual([])
    expect(out.handled).toBe(false)
  })
})
