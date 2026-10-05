// Podium addon root setup, injected at build time by the Podium builder (see builder/build.mjs). Protocol v3.
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
//    decides who may send: only the deck owner's (and co-presenters') sockets may publish; everyone else receives.
//
// 3. The bridge. This file contains no user interface. It exposes `window.__podium` (socket, navigation, laser
//    pointer, timer, follow state) to Podium's server-served `/_podium/live-ui.js`, which draws the live pill, the
//    blackout, the session HUD and the audience features for every deck kind. UI changes therefore never need the
//    deck to be rebuilt.
import { toRaw, watch } from 'vue'
import { addSyncMethod } from '@slidev/client/state/syncState.ts'
import { useNav } from '@slidev/client/composables/useNav.ts'
import { patch as patchShared, sharedState } from '@slidev/client/state/shared.ts'
import { syncDirections } from '@slidev/client/state/storage.ts'

type Handler = (data: Record<string, unknown>) => void
type Listener = (msg: Record<string, unknown>) => void
type Message = Record<string, unknown> & { t?: string }

declare const __PODIUM_SLUG__: string | undefined

export default function setupPodium() {
  if (typeof window === 'undefined') return
  installBasePathRebase()
  setupPodiumBridge()
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

function setupPodiumBridge() {
  const slug = resolveSlug()
  if (!slug) return
  // Headless export / print renders must not try to sync.
  const q = new URLSearchParams(location.search)
  if (q.has('print') || location.pathname.includes('/print')) return

  const handlers = new Map<string, Handler>()
  const lastSent = new Map<string, string>()
  const lastState = new Map<string, Record<string, unknown>>()
  const listeners = new Map<string, Set<Listener>>()
  let socket: WebSocket | null = null
  let backoff = 1000
  let closedByPage = false
  let canSend = false
  let connected = false
  const pending: string[] = []
  const nav = useNav()
  const role = () => (nav.isPresenter.value ? 'presenter' : 'play')

  // ---- Slidev navigation: executing remote-control commands and reporting the position ------------------------
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
    emit('position', position())
    if (!canSend) return
    const payload = JSON.stringify({ t: 'info', page: nav.currentSlideNo.value, total: nav.total.value, clicks: nav.clicks.value, clicksTotal: nav.clicksTotal.value, role: role() })
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
  const position = () => ({ page: nav.currentSlideNo.value, total: nav.total.value, clicks: nav.clicks.value, clicksTotal: nav.clicksTotal.value })
  watch([nav.currentSlideNo, nav.clicks, nav.clicksTotal], () => reportPosition())
  // Moving between the play route and /presenter inside the SPA changes what this window is to the room.
  watch(nav.isPresenter, () => { if (canSend) send(JSON.stringify({ t: 'hi', role: role() })); reportPosition(true); emit('role', { role: role() }) })

  // ---- Laser pointer and timer, driven from the phone --------------------------------------------------------
  // Slidev renders `sharedState.cursor` on every non-presenter window (LaserPointer.vue), so a patch here lights the
  // dot on the projector and on the audience's own screens. Coordinates are percentages of the slide.
  function applyPointer(x: unknown, y: unknown) {
    if (typeof x !== 'number' || typeof y !== 'number') { patchShared('cursor', undefined); return }
    patchShared('cursor', { x: Math.min(100, Math.max(0, x)), y: Math.min(100, Math.max(0, y)), style: 'laser' })
  }
  // The presenter view owns a ticking timer display; clicking its own controls keeps that display honest. Windows
  // without the control (play mode) patch the shared timer directly, which the remote and other windows read.
  // Patched values must be plain objects: Slidev's own BroadcastChannel sync structured-clones the state, and a
  // reactive proxy (e.g. the existing `slides` map) inside it would throw and abort every sync write.
  function applyTimer(op: unknown) {
    const raw = toRaw(sharedState.timer) as { status: 'stopped' | 'running' | 'paused'; startedAt: number; pausedAt: number } | undefined
    const timer = { status: raw?.status ?? 'stopped', startedAt: raw?.startedAt ?? 0, pausedAt: raw?.pausedAt ?? 0 }
    const running = timer.status === 'running'
    if (nav.isPresenter.value) {
      const toggle = document.querySelector('.slidev-presenter [class*="i-carbon:pause"], .slidev-presenter [class*="i-carbon:play"]')
      const reset = document.querySelector('.slidev-presenter [class*="i-carbon:renew"]')
      const click = (el: Element | null) => { if (!el) return false; el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true })); return true }
      if (op === 'reset' && click(reset)) return
      if ((op === 'toggle' || (op === 'start' && !running) || (op === 'pause' && running)) && click(toggle)) return
      if (op === 'start' || op === 'pause') return
    }
    const now = Date.now()
    const set = (status: 'stopped' | 'running' | 'paused', startedAt: number, pausedAt: number) => patchShared('timer', { status, slides: {}, startedAt, pausedAt })
    switch (op) {
      case 'reset': set('stopped', 0, 0); break
      case 'pause': if (running) set('paused', timer.startedAt, now); break
      case 'start':
      case 'toggle':
        if (running) { if (op === 'toggle') set('paused', timer.startedAt, now) }
        else if (timer.status === 'paused') set('running', now - (timer.pausedAt - timer.startedAt), 0)
        else set('running', now, 0)
        break
    }
  }

  // After a reconnect the server may have forgotten this room (restart) or dropped the cached state when the last
  // presenter left; re-send everything we last published so viewers (and the remote) are back in sync at once.
  function republish() {
    if (!canSend) return
    reportPosition(true)
    for (const [channel, state] of lastState) {
      const payload = JSON.stringify({ t: 'state', channel, state: wire(state) })
      lastSent.set(channel, payload)
      send(payload)
    }
  }

  // JSON drops `undefined`, so a cleared value (Slidev sets `cursor = undefined` when the mouse leaves the slide)
  // would never reach the other windows and the laser dot would stay lit for the audience. Send `null` instead;
  // Slidev's receiving side treats both the same.
  function wire(state: Record<string, unknown>): Record<string, unknown> {
    const out: Record<string, unknown> = {}
    for (const key of Object.keys(state)) out[key] = state[key] === undefined ? null : state[key]
    return out
  }

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
      connected = true
      while (pending.length && socket?.readyState === WebSocket.OPEN) socket.send(pending.shift()!)
      ;(window as any).__podiumSocketActive?.(true)
      emit('open', {})
    })
    socket.addEventListener('message', (ev) => {
      let msg: Message
      try { msg = JSON.parse(ev.data) } catch { return }
      switch (msg.t) {
        case 'hello':
          // The server decides who may publish; presenting instances also execute remote commands and report position.
          canSend = !!msg.canSend
          if (canSend) { send(JSON.stringify({ t: 'hi', role: role() })); republish() }
          break
        case 'build':
          // New build being served: live.js decides whether to reload now, later, or just show a notice.
          if (msg.build) (window as any).__podiumNewBuild?.(msg.build)
          break
        case 'nav':
          // Phone remote. The server already routes commands to exactly one deck window; the guard is belt and braces.
          if (canSend && typeof msg.action === 'string') void executeNav(msg.action, typeof msg.page === 'number' ? msg.page : undefined)
          break
        case 'pointer':
          if (canSend) applyPointer(msg.x, msg.y)
          break
        case 'timer':
          if (canSend) applyTimer(msg.op)
          break
        case 'state': {
          if (typeof msg.channel !== 'string' || !msg.state || typeof msg.state !== 'object') break
          const h = handlers.get(msg.channel)
          if (h) h(msg.state as Record<string, unknown>)
          break
        }
      }
      emit(String(msg.t || ''), msg)
      emit('*', msg)
    })
    socket.addEventListener('close', (ev) => {
      socket = null
      connected = false
      ;(window as any).__podiumSocketActive?.(false)
      emit('close', { code: ev.code })
      // 4403/4404 = not allowed to join (private deck, no access); 4410 = the session that admitted us has ended.
      if (ev.code === 4403 || ev.code === 4404 || ev.code === 4410) return
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

  function emit(type: string, data: Record<string, unknown>) {
    const set = listeners.get(type)
    if (!set) return
    for (const fn of [...set]) { try { fn(data) } catch { /* a listener must never break the relay */ } }
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
        const raw = toRaw(state) as Record<string, unknown>
        lastState.set(channelKey, raw)
        const payload = JSON.stringify({ t: 'state', channel: channelKey, state: wire(raw) })
        if (lastSent.get(channelKey) === payload) return
        lastSent.set(channelKey, payload)
        send(payload)
      }
    },
  })

  const following = () => (nav.isPresenter.value ? syncDirections.value.presenterReceive : syncDirections.value.viewerReceive)
  function setFollowing(on: boolean) {
    if (nav.isPresenter.value) syncDirections.value = { ...syncDirections.value, presenterReceive: on }
    else syncDirections.value = { ...syncDirections.value, viewerReceive: on }
  }

  // Offline cache: live.js registers the service worker (shared by every deck kind); nothing to do here beyond
  // exposing the current build so it can pick the right cache.
  ;(window as any).__podiumBuild = document.querySelector('meta[name="podium-build"]')?.getAttribute('content') || null

  const bridge = {
    protocol: 3,
    kind: 'slidev',
    slug,
    get role() { return role() },
    get canSend() { return canSend },
    get connected() { return connected },
    get following() { return following() },
    setFollowing,
    position,
    send(message: Record<string, unknown>) { if (canSend) send(JSON.stringify(message)) },
    on(type: string, fn: Listener) {
      let set = listeners.get(type)
      if (!set) { set = new Set(); listeners.set(type, set) }
      set.add(fn)
      return () => { set!.delete(fn) }
    },
    go(page: number, clicks?: number) { void nav.go(page, clicks) },
    nav(action: string, page?: number) { void executeNav(action, page) },
  }
  ;(window as any).__podium = bridge
  window.dispatchEvent(new CustomEvent('podium:bridge'))
}

function resolveSlug(): string | null {
  if (typeof __PODIUM_SLUG__ === 'string' && __PODIUM_SLUG__) return __PODIUM_SLUG__
  const meta = document.querySelector('meta[name="podium-slug"]')?.getAttribute('content')
  if (meta) return meta
  const m = location.pathname.match(/^\/d\/([a-z0-9][a-z0-9-]*)\//)
  return m ? m[1] : null
}
