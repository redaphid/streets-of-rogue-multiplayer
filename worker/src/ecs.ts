import type { Components, EntityRecord } from './protocol'

// Components whose updates are broadcast but never written to storage.
// High-frequency state (positions) lives only in memory; after a hibernation
// wake-up it is stale for at most one client tick.
const VOLATILE = new Set(['pos'])

export interface Entity {
  owner: number
  components: Components
}

/**
 * Authoritative ECS state for one room. Entities are integer ids; components
 * are named blobs the server treats as opaque except for ownership rules.
 * Persistence contract: spawn/despawn and non-volatile component writes are
 * mirrored to Durable Object storage so state survives hibernation/eviction.
 */
export class RoomWorld {
  private entities = new Map<number, Entity>()
  private nextId = 1

  spawn(owner: number, components: Components): number {
    const e = this.nextId++
    this.entities.set(e, { owner, components: { ...components } })
    return e
  }

  /** Re-insert an entity loaded from storage. */
  restore(e: number, owner: number, components: Components): void {
    this.entities.set(e, { owner, components })
    if (e >= this.nextId) this.nextId = e + 1
  }

  /** Ensure the id counter is at least `next` (used when rebuilding after hibernation). */
  bumpCounter(next: number): void {
    if (next > this.nextId) this.nextId = next
  }

  /** Merge component writes. Returns false if the entity is missing or not owned by `by`. */
  set(by: number, e: number, components: Components): boolean {
    const ent = this.entities.get(e)
    if (!ent || ent.owner !== by) return false
    Object.assign(ent.components, components)
    return true
  }

  despawn(by: number, e: number): boolean {
    const ent = this.entities.get(e)
    if (!ent || ent.owner !== by) return false
    this.entities.delete(e)
    return true
  }

  /** Remove every entity owned by a departed client. Returns their ids. */
  despawnOwnedBy(owner: number): number[] {
    const gone: number[] = []
    for (const [e, ent] of this.entities) {
      if (ent.owner === owner) gone.push(e)
    }
    for (const e of gone) this.entities.delete(e)
    return gone
  }

  get(e: number): Entity | undefined {
    return this.entities.get(e)
  }

  snapshot(): EntityRecord[] {
    return [...this.entities].map(([e, ent]) => ({ e, owner: ent.owner, components: ent.components }))
  }

  get idCounter(): number {
    return this.nextId
  }

  static isVolatile(component: string): boolean {
    return VOLATILE.has(component)
  }

  /** Split a component write into the part worth persisting (may be empty). */
  static persistable(components: Components): Components {
    const out: Components = {}
    for (const [k, v] of Object.entries(components)) {
      if (!VOLATILE.has(k)) out[k] = v
    }
    return out
  }
}
