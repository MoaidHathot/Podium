// Podium addon root setup, injected at build time by the Podium builder (see builder/build.mjs).
//
// 1. Base-path rebasing for dynamic media URLs. Podium serves every deck under /d/<slug>/ and builds it with that
//    base. Vite rewrites *static* asset references, but a runtime-bound `:src="'/screenshots/x.png'"` reaches the
//    browser untouched, 404s at the site root, and the deck shows a broken image or its own placeholder, while the
//    same deck works locally at base "/". When such a request fails, retry it under the base path. Correct URLs are
//    never touched; a genuinely missing file fails again on the retry and the author's own error handling proceeds.
//
// 2. Cross-device sync: an additional Slidev sync method that relays shared state over a WebSocket to the Podium
//    server. Slidev's built-in sync uses BroadcastChannel, which only reaches windows of the same browser profile.
//    With this, the presenter view on a phone or laptop drives the audience view on another machine. The server
//    decides who may send: only the deck owner's sockets are allowed to publish; everyone else receives.
import { toRaw, watch } from 'vue'
import { addSyncMethod } from '@slidev/client/state/syncState.ts'
import { useNav } from '@slidev/client/composables/useNav.ts'
import { patch as patchShared } from '@slidev/client/state/shared.ts'

type Handler = (data: Record<string, unknown>) => void

declare const __PODIUM_SLUG__: string | undefined

export default function setupPodium() {
  if (typeof window === 'undefined') return
  installBasePathRebase()
  setupPodiumSync()
}

function installBasePathRebase() {
  const base: string = import.meta.env.BASE_URL || '/'
  if (base === '/' || base === './' || base === '') return
  const prefix = base.endsWith('/') ? base.slice(0, -1) : base

  const rebased = (value: string | null): string | null => {
    if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith(base)) return null
    return prefix + value
  }

  // Resource "error" events do not bubble, but they do pass through the capture phase. Handling them at the
  // document level lets us retry before the element's own listeners (e.g. a Vue @error) see the failure.
  document.addEventListener('error', (event) => {
    const el = event.target
    if (!(el instanceof HTMLImageElement || el instanceof HTMLVideoElement || el instanceof HTMLAudioElement || el instanceof HTMLSourceElement || el instanceof HTMLTrackElement)) return
    const next = rebased(el.getAttribute('src'))
    if (!next) return
    event.stopPropagation()
    el.setAttribute('src', next)
    if (el instanceof HTMLSourceElement && el.parentElement instanceof HTMLMediaElement) el.parentElement.load()
  }, true)
}

function setupPodiumSync() {
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
  let canSend = false
  const pending: string[] = []

  // Slidev navigation, for executing remote-control commands and reporting the position to the remote.
  const nav = useNav()
  async function executeNav(action: string, page?: number) {
    switch (action) {
      case 'next': await nav.next(); break
      case 'prev': await nav.prev(); break
      case 'nextSlide': await nav.nextSlide(); break
      case 'prevSlide': await nav.prevSlide(); break
      case 'first': await nav.go(1); break
      case 'last': await nav.go(nav.total.value); break
      case 'go': if (page && page >= 1 && page <= nav.total.value) await nav.go(page); break
    }
  }
  let lastInfo = ''
  const instanceId = `podium_${Math.random().toString(36).slice(2)}`
  function reportPosition(force = false) {
    if (!canSend) return
    const payload = JSON.stringify({ t: 'info', page: nav.currentSlideNo.value, total: nav.total.value, clicks: nav.clicks.value, clicksTotal: nav.clicksTotal.value })
    if (!force && payload === lastInfo) return
    lastInfo = payload
    send(payload)
    // Slidev only publishes shared state from presenter views (and from trusted dev origins). A presenting instance
    // in plain play mode, e.g. the owner's laptop on the projector driven by a clicker or the phone remote, must
    // publish too so viewers follow. Patching the shared state triggers every sync method, including this socket.
    if (!nav.isPresenter.value) {
      patchShared('page', nav.currentSlideNo.value)
      patchShared('clicks', nav.clicks.value)
      patchShared('clicksTotal', nav.clicksTotal.value)
      patchShared('lastUpdate', { id: instanceId, type: 'presenter', time: Date.now() })
    }
  }
  watch([nav.currentSlideNo, nav.clicks, nav.clicksTotal], () => reportPosition())

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
    socket.addEventListener('open', () => { (window as any).__podiumSocketActive?.(true) })
    socket.addEventListener('message', (ev) => {
      let msg: { t?: string; channel?: string; state?: Record<string, unknown>; build?: string; canSend?: boolean; action?: string; page?: number }
      try { msg = JSON.parse(ev.data) } catch { return }
      switch (msg.t) {
        case 'hello':
          // The server decides who may publish; presenting instances also execute remote commands and report position.
          canSend = !!msg.canSend
          if (canSend) reportPosition(true)
          return
        case 'build':
          // New build being served: live.js decides whether to reload now, later, or just show a notice.
          if (msg.build) (window as any).__podiumNewBuild?.(msg.build)
          return
        case 'nav':
          // Phone remote. Only presenting instances act (viewers follow through Slidev's shared state as usual).
          if (canSend && msg.action) void executeNav(msg.action, msg.page)
          return
        case 'state': {
          if (!msg.channel || !msg.state) return
          const h = handlers.get(msg.channel)
          if (h) h(msg.state)
          return
        }
      }
    })
    socket.addEventListener('close', (ev) => {
      socket = null
      ;(window as any).__podiumSocketActive?.(false)
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
