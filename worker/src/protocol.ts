// Wire protocol between game clients and a GameRoom Durable Object.
// One JSON object per WebSocket text frame. Field names are kept short
// because position updates dominate traffic.
//
// The C# twin of this file is EightPlayers/EcsNet/Protocol.cs — keep them in sync.

export const PROTO_VERSION = 1

export type Components = Record<string, unknown>

// ---- client -> server ----

export interface HelloMsg {
  t: 'hello'
  proto: number
  name: string
}

export interface SpawnReq {
  t: 'spawn'
  /** client-chosen temporary id, echoed back so the client can map it to the server entity id */
  tmp: number
  components: Components
}

export interface SetReq {
  t: 'set'
  e: number
  components: Components
}

export interface DespawnReq {
  t: 'despawn'
  e: number
}

export interface PingMsg {
  t: 'ping'
  ts: number
}

/** Claim the room's shared world seed. First write wins; later writes are ignored. */
export interface WorldReq {
  t: 'world'
  seed: string
}

export type ClientMsg = HelloMsg | SpawnReq | SetReq | DespawnReq | PingMsg | WorldReq

// ---- server -> client ----

export interface EntityRecord {
  e: number
  owner: number
  components: Components
}

export interface WelcomeMsg {
  t: 'welcome'
  proto: number
  you: number
  room: string
  peers: { id: number; name: string }[]
  snapshot: EntityRecord[]
  /** null until some client claims the room's world seed */
  world: { seed: string } | null
}

export interface WorldEvt {
  t: 'world'
  seed: string
}

export interface SpawnEvt {
  t: 'spawn'
  e: number
  owner: number
  components: Components
  /** present only on the copy sent to the owner */
  tmp?: number
}

export interface SetEvt {
  t: 'set'
  e: number
  components: Components
}

export interface DespawnEvt {
  t: 'despawn'
  e: number
}

export interface PeerEvt {
  t: 'peer'
  id: number
  name: string
  joined: boolean
}

export interface PongMsg {
  t: 'pong'
  ts: number
}

export interface ErrorMsg {
  t: 'error'
  message: string
}

export type ServerMsg = WelcomeMsg | SpawnEvt | SetEvt | DespawnEvt | PeerEvt | PongMsg | ErrorMsg | WorldEvt
