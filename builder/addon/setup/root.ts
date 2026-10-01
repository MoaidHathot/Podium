// Podium sync: an additional Slidev sync method that relays shared state over a WebSocket to the Podium server.
// Slidev's built-in sync uses BroadcastChannel, which only reaches windows of the same browser profile. With this,
// the presenter view on a phone or laptop drives the audience view on another machine.
//
// Injected at build time by the Podium builder (see builder/build.mjs). The server decides who may send: only the
// deck owner's sockets are allowed to publish; everyone else receives.
import { toRaw } from 'vue'
import { addSyncMethod } from '@slidev/client/state/syncState.ts'

type Handler = (data: Record<string, unknown>) => void

declare const __PODIUM_SLUG__: string | undefined

export default function setupPodiumSync() {
  if (typeof window === 'undefined') return
  const slug = resolveSlug()
  if (!slug) return
  // Headless export / print renders must not try to sync.
  const q = new URLSearchParams(location.search)
  if (q.has('print') || location.pathname.includes('/print')) return

  const handlers = new Map<string, Handler>()
  const lastSent = new Map<string, string>()
  let socket: WebSocket | null = null
  let backoff = 1000
  let closedByPage = false
  const pending: string[] = []

  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/sync/${encodeURIComponent(slug)}`

  function connect() {
    if (closedByPage) return
    try {
      socket = new WebSocket(url)
    } catch {
      scheduleReconnect()
      return
    }
    socket.addEventListener('open', () => {
      backoff = 1000
      while (pending.length && socket?.readyState === WebSocket.OPEN) socket.send(pending.shift()!)
    })
    socket.addEventListener('message', (ev) => {
      let msg: { t?: string; channel?: string; state?: Record<string, unknown> }
      try { msg = JSON.parse(ev.data) } catch { return }
      if (msg.t !== 'state' || !msg.channel || !msg.state) return
      const h = handlers.get(msg.channel)
      if (h) h(msg.state)
    })
    socket.addEventListener('close', (ev) => {
      socket = null
      // 4403 = not allowed to join (private deck, no access); do not retry.
      if (ev.code === 4403 || ev.code === 4404) return
      scheduleReconnect()
    })
    socket.addEventListener('error', () => { /* close follows */ })
  }

  function scheduleReconnect() {
    if (closedByPage) return
    setTimeout(connect, backoff)
    backoff = Math.min(backoff * 2, 30000)
  }

  function send(payload: string) {
    if (socket && socket.readyState === WebSocket.OPEN) socket.send(payload)
    else { pending.push(payload); if (pending.length > 20) pending.shift() }
  }

  window.addEventListener('pagehide', () => { closedByPage = true; socket?.close() })

  addSyncMethod({
    enabled: true,
    init(channelKey, onUpdate, _state, _persist) {
      handlers.set(channelKey, onUpdate as Handler)
      if (!socket) connect()
      return (state, updating) => {
        // `updating` is true while applying a remote update; never echo those back.
        if (updating) return
        const payload = JSON.stringify({ t: 'state', channel: channelKey, state: toRaw(state) })
        if (lastSent.get(channelKey) === payload) return
        lastSent.set(channelKey, payload)
        send(payload)
      }
    },
  })
}

function resolveSlug(): string | null {
  if (typeof __PODIUM_SLUG__ === 'string' && __PODIUM_SLUG__) return __PODIUM_SLUG__
  const meta = document.querySelector('meta[name="podium-slug"]')?.getAttribute('content')
  if (meta) return meta
  const m = location.pathname.match(/^\/d\/([a-z0-9][a-z0-9-]*)\//)
  return m ? m[1] : null
}
