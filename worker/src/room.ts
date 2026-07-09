import { DurableObject } from 'cloudflare:workers'
import { RoomWorld } from './ecs'
import { PROTO_VERSION } from './protocol'
import { runSystems } from './systems'
import type { ClientMsg, Components, ServerMsg } from './protocol'

interface Attachment {
  id: number
  name: string
  helloed: boolean
}

interface Meta {
  nextClient: number
}

interface StoredEntity {
  owner: number
  components: Components
}

/**
 * One Durable Object per game room. Holds the authoritative ECS world and
 * fans component updates out to every connected client over hibernatable
 * WebSockets. Room identity comes from idFromName(roomCode) in the router.
 */
export class GameRoom extends DurableObject {
  private world = new RoomWorld()
  private roomName = ''
  private nextClient = 1
  private worldSeed: string | null = null
  private worldNum = 1

  constructor(ctx: DurableObjectState, env: unknown) {
    super(ctx, env as never)
    // Rebuild in-memory state before any message is delivered after a wake-up.
    ctx.blockConcurrencyWhile(async () => {
      const meta = await ctx.storage.get<Meta>('meta')
      if (meta) this.nextClient = meta.nextClient
      this.roomName = (await ctx.storage.get<string>('room')) ?? ''
      this.worldSeed = (await ctx.storage.get<string>('worldSeed')) ?? null
      this.worldNum = (await ctx.storage.get<number>('worldNum')) ?? 1
      const stored = await ctx.storage.list<StoredEntity>({ prefix: 'e:' })
      for (const [key, ent] of stored) {
        this.world.restore(Number(key.slice(2)), ent.owner, ent.components)
      }
      const nextEntity = await ctx.storage.get<number>('nextEntity')
      if (nextEntity) this.world.bumpCounter(nextEntity)
    })
  }

  async fetch(request: Request): Promise<Response> {
    if (request.headers.get('Upgrade') !== 'websocket') {
      return new Response('expected websocket', { status: 426 })
    }
    const url = new URL(request.url)
    const room = url.searchParams.get('room') ?? this.roomName
    if (!this.roomName && room) {
      this.roomName = room
      await this.ctx.storage.put('room', room)
    }

    const pair = new WebSocketPair()
    const [client, server] = Object.values(pair)

    const id = this.nextClient++
    await this.ctx.storage.put('meta', { nextClient: this.nextClient } satisfies Meta)
    server.serializeAttachment({ id, name: '', helloed: false } satisfies Attachment)
    this.ctx.acceptWebSocket(server)

    return new Response(null, { status: 101, webSocket: client })
  }

  async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    if (typeof raw !== 'string') return
    let msg: ClientMsg
    try {
      msg = JSON.parse(raw) as ClientMsg
    } catch {
      this.send(ws, { t: 'error', message: 'bad json' })
      return
    }
    const att = ws.deserializeAttachment() as Attachment

    switch (msg.t) {
      case 'hello': {
        if (msg.proto !== PROTO_VERSION) {
          this.send(ws, { t: 'error', message: `protocol mismatch: server=${PROTO_VERSION} client=${msg.proto}` })
          ws.close(1002, 'protocol mismatch')
          return
        }
        att.name = String(msg.name ?? '').slice(0, 32)
        att.helloed = true
        ws.serializeAttachment(att)
        this.send(ws, {
          t: 'welcome',
          proto: PROTO_VERSION,
          you: att.id,
          room: this.roomName,
          peers: this.peers(att.id),
          snapshot: this.world.snapshot(),
          world: this.worldSeed !== null ? { seed: this.worldSeed, num: this.worldNum } : null,
        })
        this.broadcast({ t: 'peer', id: att.id, name: att.name, joined: true }, ws)
        return
      }
      case 'spawn': {
        if (!att.helloed) return
        const e = this.world.spawn(att.id, msg.components)
        await this.persistEntity(e)
        this.send(ws, { t: 'spawn', e, owner: att.id, components: msg.components, tmp: msg.tmp })
        this.broadcast({ t: 'spawn', e, owner: att.id, components: msg.components }, ws)
        return
      }
      case 'set': {
        if (!att.helloed) return
        if (!this.world.set(att.id, msg.e, msg.components)) {
          this.send(ws, { t: 'error', message: `set rejected for entity ${msg.e}` })
          return
        }
        const durable = RoomWorld.persistable(msg.components)
        if (Object.keys(durable).length > 0) await this.persistEntity(msg.e)
        this.broadcast({ t: 'set', e: msg.e, components: msg.components }, ws)
        return
      }
      case 'setm': {
        if (!att.helloed || !Array.isArray(msg.updates)) return
        const applied: { e: number; components: Components }[] = []
        for (const update of msg.updates.slice(0, 256)) {
          if (!update || typeof update.e !== 'number' || !update.components) continue
          if (!this.world.set(att.id, update.e, update.components)) continue
          applied.push({ e: update.e, components: update.components })
          const durable = RoomWorld.persistable(update.components)
          if (Object.keys(durable).length > 0) await this.persistEntity(update.e)
        }
        if (applied.length > 0) this.broadcast({ t: 'setm', updates: applied }, ws)
        return
      }
      case 'despawn': {
        if (!att.helloed) return
        if (!this.world.despawn(att.id, msg.e)) return
        await this.ctx.storage.delete(`e:${msg.e}`)
        this.broadcast({ t: 'despawn', e: msg.e }, ws)
        return
      }
      case 'world': {
        if (!att.helloed) return
        let changed = false
        const seed = String(msg.seed ?? '').slice(0, 64)
        if (seed && this.worldSeed === null) {
          this.worldSeed = seed
          await this.ctx.storage.put('worldSeed', seed)
          changed = true
        }
        const num = Number(msg.num ?? 0)
        if (this.worldSeed !== null && Number.isInteger(num) && num > this.worldNum) {
          this.worldNum = num
          await this.ctx.storage.put('worldNum', num)
          changed = true
        }
        if (this.worldSeed === null) return
        const evt = { t: 'world', seed: this.worldSeed, num: this.worldNum } as const
        if (changed) this.broadcast(evt, ws)
        // Always answer the sender with the authoritative state (covers
        // lost seed races and stale num proposals).
        this.send(ws, evt)
        return
      }
      case 'event': {
        if (!att.helloed) return
        const kind = String(msg.kind ?? '').slice(0, 32)
        if (!kind) return
        this.broadcast({ t: 'event', from: att.id, kind, data: msg.data }, ws)
        // Server-side game logic: systems may turn this event into
        // authoritative component writes, broadcast to EVERYONE including
        // the sender (outcomes are server truth, not echoes).
        const result = runSystems(this.world, {
          from: att.id,
          kind,
          data: msg.data as Record<string, unknown> | undefined,
        })
        for (const set of result.sets) {
          const durable = RoomWorld.persistable(set.components)
          if (Object.keys(durable).length > 0) await this.persistEntity(set.e)
          this.broadcast({ t: 'set', e: set.e, components: set.components })
        }
        return
      }
      case 'ping':
        this.send(ws, { t: 'pong', ts: msg.ts })
        return
    }
  }

  async webSocketClose(ws: WebSocket): Promise<void> {
    await this.dropClient(ws)
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    await this.dropClient(ws)
  }

  private async dropClient(ws: WebSocket): Promise<void> {
    const att = ws.deserializeAttachment() as Attachment | null
    if (!att) return
    const gone = this.world.despawnOwnedBy(att.id)
    for (const e of gone) {
      await this.ctx.storage.delete(`e:${e}`)
      this.broadcast({ t: 'despawn', e }, ws)
    }
    if (att.helloed) this.broadcast({ t: 'peer', id: att.id, name: att.name, joined: false }, ws)

    // Last player out: release the world seed so the room can host a fresh game.
    const remaining = this.ctx.getWebSockets().filter((other) => other !== ws)
    if (remaining.length === 0 && this.worldSeed !== null) {
      this.worldSeed = null
      this.worldNum = 1
      await this.ctx.storage.delete('worldSeed')
      await this.ctx.storage.delete('worldNum')
    }
  }

  private async persistEntity(e: number): Promise<void> {
    const ent = this.world.get(e)
    if (!ent) return
    await this.ctx.storage.put(`e:${e}`, {
      owner: ent.owner,
      components: RoomWorld.persistable(ent.components),
    } satisfies StoredEntity)
    await this.ctx.storage.put('nextEntity', this.world.idCounter)
  }

  private peers(excludeId: number): { id: number; name: string }[] {
    const out: { id: number; name: string }[] = []
    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment | null
      if (att && att.helloed && att.id !== excludeId) out.push({ id: att.id, name: att.name })
    }
    return out
  }

  private send(ws: WebSocket, msg: ServerMsg): void {
    try {
      ws.send(JSON.stringify(msg))
    } catch {
      // socket already closing; close handler will clean up
    }
  }

  private broadcast(msg: ServerMsg, except?: WebSocket): void {
    const data = JSON.stringify(msg)
    for (const ws of this.ctx.getWebSockets()) {
      if (ws === except) continue
      const att = ws.deserializeAttachment() as Attachment | null
      if (!att?.helloed) continue
      try {
        ws.send(data)
      } catch {
        // ignore; close handler cleans up
      }
    }
  }
}
