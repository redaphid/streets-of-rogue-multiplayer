import { describe, expect, it } from 'vitest'
import { RoomWorld } from './ecs'

describe('RoomWorld', () => {
  it('assigns sequential entity ids starting at 1', () => {
    const w = new RoomWorld()
    expect(w.spawn(10, {})).toBe(1)
    expect(w.spawn(10, {})).toBe(2)
  })

  it('enforces ownership on set', () => {
    const w = new RoomWorld()
    const e = w.spawn(1, { pos: { x: 0, y: 0 } })
    expect(w.set(1, e, { pos: { x: 5, y: 5 } })).toBe(true)
    expect(w.set(2, e, { pos: { x: 9, y: 9 } })).toBe(false)
    expect(w.get(e)?.components.pos).toEqual({ x: 5, y: 5 })
  })

  it('merges component writes instead of replacing the set', () => {
    const w = new RoomWorld()
    const e = w.spawn(1, { player: { name: 'a' }, pos: { x: 0, y: 0 } })
    w.set(1, e, { pos: { x: 2, y: 3 } })
    expect(w.get(e)?.components.player).toEqual({ name: 'a' })
  })

  it('enforces ownership on despawn', () => {
    const w = new RoomWorld()
    const e = w.spawn(1, {})
    expect(w.despawn(2, e)).toBe(false)
    expect(w.despawn(1, e)).toBe(true)
    expect(w.get(e)).toBeUndefined()
  })

  it('despawnOwnedBy removes exactly that owner and reports ids', () => {
    const w = new RoomWorld()
    const a1 = w.spawn(1, {})
    const b = w.spawn(2, {})
    const a2 = w.spawn(1, {})
    expect(w.despawnOwnedBy(1).sort()).toEqual([a1, a2].sort())
    expect(w.snapshot().map((r) => r.e)).toEqual([b])
  })

  it('snapshot reflects live component state', () => {
    const w = new RoomWorld()
    const e = w.spawn(4, { pos: { x: 1, y: 1 } })
    w.set(4, e, { pos: { x: 7, y: 8 } })
    expect(w.snapshot()).toEqual([{ e, owner: 4, components: { pos: { x: 7, y: 8 } } }])
  })

  it('restore + bumpCounter keeps new ids clear of restored ones', () => {
    const w = new RoomWorld()
    w.restore(5, 1, {})
    w.bumpCounter(9)
    expect(w.spawn(1, {})).toBe(9)
    expect(w.get(5)?.owner).toBe(1)
  })

  it('pos is volatile, other components persist', () => {
    expect(RoomWorld.isVolatile('pos')).toBe(true)
    expect(RoomWorld.isVolatile('player')).toBe(false)
    expect(RoomWorld.persistable({ pos: { x: 1, y: 2 }, player: { name: 'n' } })).toEqual({
      player: { name: 'n' },
    })
  })

  it('input is a shared component: any client may write it on any entity', () => {
    const w = new RoomWorld()
    const e = w.spawn(1, { player: { name: 'a' }, pos: { x: 0, y: 0 } })
    expect(w.set(2, e, { input: { tx: 5, ty: 6 } })).toBe(true)
    expect(w.get(e)?.components.input).toEqual({ tx: 5, ty: 6 })
  })

  it('a write mixing input with owned components is still rejected for non-owners', () => {
    const w = new RoomWorld()
    const e = w.spawn(1, { pos: { x: 0, y: 0 } })
    expect(w.set(2, e, { input: { tx: 5, ty: 6 }, pos: { x: 9, y: 9 } })).toBe(false)
    expect(w.get(e)?.components.pos).toEqual({ x: 0, y: 0 })
    expect(w.get(e)?.components.input).toBeUndefined()
  })

  it('input is volatile: broadcast but never persisted', () => {
    expect(RoomWorld.isVolatile('input')).toBe(true)
    expect(RoomWorld.persistable({ input: { tx: 1, ty: 2 } })).toEqual({})
  })
})
