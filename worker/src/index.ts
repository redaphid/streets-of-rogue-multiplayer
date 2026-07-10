import { GameRoom } from './room'

export { GameRoom }

export interface Env {
  ROOMS: DurableObjectNamespace
}

const ROOM_CODE = /^[a-zA-Z0-9-]{1,32}$/

// Routes:
//   GET /                          health check
//   GET /room/<code>/ws            WebSocket upgrade, forwarded to that room's Durable Object
export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url)
    const parts = url.pathname.split('/').filter(Boolean)

    if (parts.length === 0) {
      return new Response('sor-ecs-net ok\n')
    }

    if (parts.length === 3 && parts[0] === 'room' && parts[2] === 'ws') {
      const code = parts[1].toUpperCase()
      if (!ROOM_CODE.test(code)) {
        return new Response('bad room code', { status: 400 })
      }
      const stub = env.ROOMS.get(env.ROOMS.idFromName(code))
      const forward = new URL(request.url)
      forward.searchParams.set('room', code)
      return stub.fetch(new Request(forward.toString(), request))
    }

    return new Response('not found', { status: 404 })
  },
}
