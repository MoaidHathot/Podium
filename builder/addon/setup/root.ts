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
//    decides who may send: only the deck owner's (and co-presenters') sockets may publish; everyone else receives.
//
// 3. Audience affordances: a small "Live" pill showing that the deck is being presented (with the viewer count for
//    presenters), a way to browse freely and jump back to the presenter's slide, and a blackout/message overlay the
//    presenter can raise from the phone remote.
import { toRaw, watch } from 'vue'
import { addSyncMethod } from '@slidev/client/state/syncState.ts'
import { useNav } from '@slidev/client/composables/useNav.ts'
import { patch as patchShared } from '@slidev/client/state/shared.ts'
import { syncDirections } from '@slidev/client/state/storage.ts'

type Handler = (data: Record<string, unknown>) => void
type Message = {
  t?: string
  channel?: string
  state?: Record<string, unknown>
  build?: string
  canSend?: boolean
  presenters?: number
  viewers?: number
  action?: string
  page?: number
  mode?: string
  text?: string
}

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
  const lastState = new Map<string, Record<string, unknown>>()
  let socket: WebSocket | null = null
  let backoff = 1000
  let closedByPage = false
  let canSend = false
  let presenters = 0
  let viewers = 0
  let presenterPage: number | null = null
  let presenterClicks = 0
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
  watch([nav.currentSlideNo, nav.clicks, nav.clicksTotal], () => { reportPosition(); updatePill() })

  // After a reconnect the server may have forgotten this room (restart) or dropped the cached state when the last
  // presenter left; re-send everything we last published so viewers (and the remote) are back in sync at once.
  function republish() {
    if (!canSend) return
    reportPosition(true)
    for (const [channel, state] of lastState) {
      const payload = JSON.stringify({ t: 'state', channel, state })
      lastSent.set(channel, payload)
      send(payload)
    }
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
      while (pending.length && socket?.readyState === WebSocket.OPEN) socket.send(pending.shift()!)
      ;(window as any).__podiumSocketActive?.(true)
    })
    socket.addEventListener('message', (ev) => {
      let msg: Message
      try { msg = JSON.parse(ev.data) } catch { return }
      switch (msg.t) {
        case 'hello':
          // The server decides who may publish; presenting instances also execute remote commands and report position.
          canSend = !!msg.canSend
          presenters = msg.presenters ?? 0
          if (canSend) republish()
          updatePill()
          return
        case 'presence':
          presenters = msg.presenters ?? 0
          viewers = msg.viewers ?? 0
          updatePill()
          return
        case 'screen':
          // Blackout / message raised by a presenter. Presenting instances keep their own view untouched.
          if (!canSend) showScreen(msg.mode, msg.text)
          return
        case 'build':
          // New build being served: live.js decides whether to reload now, later, or just show a notice.
          if (msg.build) (window as any).__podiumNewBuild?.(msg.build)
          return
        case 'nav':
          // Phone remote. Only presenting instances act (viewers follow through Slidev's shared state as usual).
          if (canSend && msg.action) void executeNav(msg.action, msg.page)
          return
        case 'info':
          if (typeof msg.page === 'number') { presenterPage = msg.page; presenterClicks = Number((msg as any).clicks) || 0; updatePill() }
          return
        case 'state': {
          if (!msg.channel || !msg.state) return
          if (typeof msg.state.page === 'number') { presenterPage = msg.state.page as number; presenterClicks = Number(msg.state.clicks) || 0; updatePill() }
          const h = handlers.get(msg.channel)
          if (h) h(msg.state)
          return
        }
      }
    })
    socket.addEventListener('close', (ev) => {
      socket = null
      ;(window as any).__podiumSocketActive?.(false)
      updatePill()
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
        const raw = toRaw(state) as Record<string, unknown>
        lastState.set(channelKey, raw)
        const payload = JSON.stringify({ t: 'state', channel: channelKey, state: raw })
        if (lastSent.get(channelKey) === payload) return
        lastSent.set(channelKey, payload)
        send(payload)
      }
    },
  })

  // ---- Live pill: shows that a presenter is driving, lets a viewer browse freely and jump back to live. ----------
  let pill: HTMLDivElement | null = null
  let pillLabel: HTMLSpanElement | null = null
  let pillButton: HTMLButtonElement | null = null
  const following = () => (nav.isPresenter.value ? syncDirections.value.presenterReceive : syncDirections.value.viewerReceive)

  function ensurePill() {
    if (pill) return
    pill = document.createElement('div')
    pill.id = 'podium-live'
    pill.setAttribute('role', 'status')
    Object.assign(pill.style, {
      position: 'fixed', left: '12px', bottom: '12px', zIndex: '2147483000', display: 'none', alignItems: 'center', gap: '8px',
      padding: '6px 10px 6px 12px', borderRadius: '999px', background: 'rgba(17,17,19,.86)', color: '#fff', font: '500 12px/1 system-ui, sans-serif',
      boxShadow: '0 4px 16px rgba(0,0,0,.35)', backdropFilter: 'blur(6px)', pointerEvents: 'auto', userSelect: 'none',
    } as CSSStyleDeclaration)
    const dot = document.createElement('span')
    Object.assign(dot.style, { width: '8px', height: '8px', borderRadius: '50%', background: '#f0506e', boxShadow: '0 0 0 3px rgba(240,80,110,.25)', display: 'inline-block' } as CSSStyleDeclaration)
    pillLabel = document.createElement('span')
    pillButton = document.createElement('button')
    Object.assign(pillButton.style, { border: '0', borderRadius: '999px', padding: '4px 9px', background: 'rgba(255,255,255,.14)', color: '#fff', font: 'inherit', cursor: 'pointer' } as CSSStyleDeclaration)
    pillButton.addEventListener('click', () => {
      if (following()) {
        setFollowing(false)
      } else {
        setFollowing(true)
        // Re-attach where the presenter is now; Slidev's own onPatch handler keeps following from here.
        if (presenterPage) void nav.go(presenterPage, presenterClicks)
      }
      updatePill()
    })
    pill.append(dot, pillLabel, pillButton)
    document.body.appendChild(pill)
  }

  function setFollowing(on: boolean) {
    if (nav.isPresenter.value) syncDirections.value = { ...syncDirections.value, presenterReceive: on }
    else syncDirections.value = { ...syncDirections.value, viewerReceive: on }
  }

  function updatePill() {
    const live = presenters > 0 && !!socket
    if (!live && !pill) return
    ensurePill()
    if (!pill || !pillLabel || !pillButton) return
    if (!live) { pill.style.display = 'none'; return }
    pill.style.display = 'flex'
    if (canSend) {
      pillLabel.textContent = `Live · ${viewers} watching`
      pillButton.style.display = 'none'
      return
    }
    pillButton.style.display = ''
    if (following()) {
      pillLabel.textContent = 'Live · following'
      pillButton.textContent = 'Browse freely'
    } else {
      const behind = presenterPage && presenterPage !== nav.currentSlideNo.value ? ` · presenter on ${presenterPage}` : ''
      pillLabel.textContent = `Live · browsing${behind}`
      pillButton.textContent = 'Jump to live'
    }
  }

  // ---- Blackout / message overlay (presenter-driven) -----------------------------------------------------------
  let screen: HTMLDivElement | null = null
  function showScreen(mode?: string, text?: string) {
    if (mode !== 'black' && mode !== 'message') { screen?.remove(); screen = null; return }
    if (!screen) {
      screen = document.createElement('div')
      screen.id = 'podium-screen'
      Object.assign(screen.style, {
        position: 'fixed', inset: '0', zIndex: '2147482000', background: '#000', color: '#ddd', display: 'flex', alignItems: 'center', justifyContent: 'center',
        padding: '8vw', textAlign: 'center', font: '500 clamp(20px, 4vw, 48px)/1.3 system-ui, sans-serif',
      } as CSSStyleDeclaration)
      document.body.appendChild(screen)
    }
    screen.textContent = mode === 'message' ? String(text || '').slice(0, 300) : ''
  }

  // Offline cache: live.js registers the service worker (shared by every deck kind); nothing to do here beyond
  // exposing the current build so it can pick the right cache.
  ;(window as any).__podiumBuild = document.querySelector('meta[name="podium-build"]')?.getAttribute('content') || null
}

function resolveSlug(): string | null {
  if (typeof __PODIUM_SLUG__ === 'string' && __PODIUM_SLUG__) return __PODIUM_SLUG__
  const meta = document.querySelector('meta[name="podium-slug"]')?.getAttribute('content')
  if (meta) return meta
  const m = location.pathname.match(/^\/d\/([a-z0-9][a-z0-9-]*)\//)
  return m ? m[1] : null
}
