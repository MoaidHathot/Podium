// Podium phone remote: sends navigation commands over the deck's sync socket and shows the position reported by the
// presenting instances. Only presenters reach this page (server-enforced) and only their sockets may publish.
(function () {
  'use strict';
  const slug = document.body.dataset.slug;
  const $ = (id) => document.getElementById(id);
  const conn = $('conn'), page = $('page'), total = $('total'), clicks = $('clicks');
  let socket = null, backoff = 1000, canSend = false, closed = false;
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/sync/${encodeURIComponent(slug)}`;

  function setConn(text, cls) { conn.textContent = text; conn.className = 'conn ' + (cls || ''); }

  function connect() {
    if (closed) return;
    try { socket = new WebSocket(url); } catch { return retry(); }
    socket.addEventListener('open', () => { backoff = 1000; setConn('connected', 'ok'); });
    socket.addEventListener('message', (ev) => {
      let m; try { m = JSON.parse(ev.data); } catch { return; }
      if (m.t === 'hello') { canSend = !!m.canSend; if (!canSend) setConn('view only', 'bad'); return; }
      if (m.t === 'info') { show(m.page, m.total, m.clicks, m.clicksTotal); return; }
      if (m.t === 'state' && m.state && typeof m.state.page === 'number') { show(m.state.page, null, m.state.clicks, m.state.clicksTotal); }
    });
    socket.addEventListener('close', (ev) => { socket = null; setConn('disconnected', 'bad'); if (ev.code !== 4403 && ev.code !== 4404) retry(); });
    socket.addEventListener('error', () => {});
  }
  function retry() { if (closed) return; setTimeout(connect, backoff); backoff = Math.min(backoff * 2, 15000); }

  function show(p, t, c, ct) {
    if (typeof p === 'number') page.textContent = String(p);
    if (typeof t === 'number') total.textContent = String(t);
    clicks.textContent = typeof ct === 'number' && ct > 0 ? `click ${c ?? 0} / ${ct}` : '';
  }

  function nav(action, extra) {
    if (!socket || socket.readyState !== WebSocket.OPEN) return;
    if (!canSend) return;
    socket.send(JSON.stringify(Object.assign({ t: 'nav', action }, extra || {})));
    if (navigator.vibrate) navigator.vibrate(10);
  }

  $('next').addEventListener('click', () => nav('next'));
  $('prev').addEventListener('click', () => nav('prev'));
  $('first').addEventListener('click', () => nav('first'));
  document.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowRight' || e.key === ' ' || e.key === 'PageDown') { e.preventDefault(); nav('next'); }
    if (e.key === 'ArrowLeft' || e.key === 'PageUp') { e.preventDefault(); nav('prev'); }
    if (e.key === 'Home') nav('first');
    if (e.key === 'End') nav('last');
  });

  // Local timer (independent of Slidev's presenter timer).
  let startedAt = 0, accumulated = 0, ticking = null;
  const timerEl = $('timer'), toggle = $('timer-toggle');
  function render() { const ms = accumulated + (startedAt ? Date.now() - startedAt : 0); const s = Math.floor(ms / 1000); timerEl.textContent = `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; }
  toggle.addEventListener('click', () => {
    if (startedAt) { accumulated += Date.now() - startedAt; startedAt = 0; clearInterval(ticking); ticking = null; toggle.textContent = '▶ Timer'; timerEl.classList.remove('running'); }
    else { startedAt = Date.now(); ticking = setInterval(render, 500); toggle.textContent = '⏸ Timer'; timerEl.classList.add('running'); }
    render();
  });
  $('timer-reset').addEventListener('click', () => { accumulated = 0; if (startedAt) startedAt = Date.now(); render(); });

  // Keep the screen awake while presenting, when the browser allows it.
  let wakeLock = null;
  async function keepAwake() { try { if ('wakeLock' in navigator) wakeLock = await navigator.wakeLock.request('screen'); } catch {} }
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') keepAwake(); });
  keepAwake();
  window.addEventListener('pagehide', () => { closed = true; socket?.close(); });
  connect();
})();
