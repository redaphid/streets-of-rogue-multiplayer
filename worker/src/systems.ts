import type { RoomWorld } from './ecs'
import type { Components } from './protocol'

// Game logic that runs HERE, in the room's Durable Object, instead of inside
// any game client. Clients publish intents/events; systems compute the
// authoritative outcome as component writes. The room applies each returned
// set to the world, persists it, and broadcasts it to every client
// (including the event's sender — outcomes are server truth, not echoes).

export interface GameEvent {
  from: number
  kind: string
  data?: Record<string, unknown>
}

export interface SystemResult {
  /** Authoritative component writes produced by the event. */
  sets: { e: number; components: Components }[]
  /** True if a system owned this event kind (it is still broadcast either way). */
  handled: boolean
}

interface Hp {
  cur: number
  max: number
}

/**
 * Damage system: a `pvp-hit` on a player entity reduces its hp component
 * server-side, clamped at 0, and marks the entity dead at 0. Replaces the
 * client-side owner-applies-damage relay — every client (and the owner)
 * learns the outcome from the same authoritative write.
 */
function damageSystem(world: RoomWorld, event: GameEvent): SystemResult {
  const e = Number(event.data?.e)
  const dmg = Number(event.data?.dmg)
  const empty = { sets: [], handled: true }
  if (!Number.isFinite(dmg) || dmg <= 0) return empty
  const ent = world.get(e)
  const hp = ent?.components.hp as Hp | undefined
  if (!ent || !hp || typeof hp.cur !== 'number') return empty
  if (ent.components.dead) return empty

  const cur = Math.max(0, hp.cur - dmg)
  const components: Components = { hp: { cur, max: hp.max } }
  if (cur === 0) components.dead = { v: true }
  world.set(ent.owner, e, components)
  return { sets: [{ e, components }], handled: true }
}

const SYSTEMS: Record<string, (world: RoomWorld, event: GameEvent) => SystemResult> = {
  'pvp-hit': damageSystem,
}

/** Run the system owning this event kind, if any. */
export function runSystems(world: RoomWorld, event: GameEvent): SystemResult {
  const system = SYSTEMS[event.kind]
  if (!system) return { sets: [], handled: false }
  return system(world, event)
}
