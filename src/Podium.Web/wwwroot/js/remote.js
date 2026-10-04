// Podium phone remote: sends navigation commands over the deck's sync socket and shows the position reported by the
// presenting instances. Only presenters reach this page (server-enforced) and only their sockets may publish.
// v2: speaker notes (current + next), go-to grid from the slide sheet, presence, blackout/message, countdown.
(function () {
  'use strict';
  const slug = document.body.dataset.slug;
  const hasNotes = document.body.dataset.hasNotes === '1';
  const hasSheet = document.body.dataset.hasSheet === '1';
  const build = document.body.dataset.build || '';
  const $ = (id) => document.getElementById(id);
  const conn = $('conn'), page = $('page'), total = $('total'), clicks = $('clicks'), presence = $('presence');
  let socket = null, backoff = 1000, canSend = false, closed = false;
  let current = 0, totalPages = 0;
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/sync/${encodeURIComponent(slug)}`;

  function setConn(text, cls) { conn.textContent = text; conn.className = 'conn ' + (cls || ''); }

  function connect() {
    if (closed) return;
    try { socket = new WebSocket(url); } catch { return retry(); }
    socket.addEventListener('open', () => { backoff = 1000; setConn('connected', 'ok'); });
    socket.addEventListener('message', (ev) => {
      let m; try { m = JSON.parse(ev.data); } catch { return; }
      switch (m.t) {
        case 'hello': canSend = !!m.canSend; if (!canSend) setConn('view only', 'bad'); showPresence(m.presenters, m.viewers); return;
        case 'presence': showPresence(m.presenters, m.viewers); return;
        case 'info': show(m.page, m.total, m.clicks, m.clicksTotal); return;
        case 'state': if (m.state && typeof m.state.page === 'number') show(m.state.page, null, m.state.clicks, m.state.clicksTotal); return;
        case 'screen': $('black').setAttribute('aria-pressed', String(m.mode === 'black')); return;
      }
    });
    socket.addEventListener('close', (ev) => { socket = null; setConn('disconnected', 'bad'); if (ev.code !== 4403 && ev.code !== 4404) retry(); });
    socket.addEventListener('error', () => {});
  }
  function retry() { if (closed) return; setTimeout(connect, backoff); backoff = Math.min(backoff * 2, 15000); }

  function showPresence(presenters, viewers) {
    if (typeof viewers !== 'number') return;
    // This remote is itself a presenter socket; count the other presenting instances (projector, laptop) separately.
    const others = Math.max(0, (presenters || 1) - 1);
    presence.textContent = `${viewers} watching${others ? ` · ${others} presenting` : ''}`;
  }

  function show(p, t, c, ct) {
    if (typeof p === 'number') { current = p; page.textContent = String(p); }
    if (typeof t === 'number') { totalPages = t; total.textContent = String(t); }
    clicks.textContent = typeof ct === 'number' && ct > 0 ? `click ${c ?? 0} / ${ct}` : '';
    renderNotes();
    markCurrentInGrid();
  }

  function send(msg) {
    if (!socket || socket.readyState !== WebSocket.OPEN || !canSend) return false;
    socket.send(JSON.stringify(msg));
    return true;
  }
  function nav(action, extra) {
    if (send(Object.assign({ t: 'nav', action }, extra || {})) && navigator.vibrate) navigator.vibrate(10);
  }

  $('next').addEventListener('click', () => nav('next'));
  $('prev').addEventListener('click', () => nav('prev'));
  $('first').addEventListener('click', () => nav('first'));
  document.addEventListener('keydown', (e) => {
    if (document.querySelector('dialog[open]')) return;
    if (e.key === 'ArrowRight' || e.key === ' ' || e.key === 'PageDown') { e.preventDefault(); nav('next'); }
    if (e.key === 'ArrowLeft' || e.key === 'PageUp') { e.preventDefault(); nav('prev'); }
    if (e.key === 'Home') nav('first');
    if (e.key === 'End') nav('last');
    if (e.key === 'b' || e.key === 'B') toggleBlack();
    if ((e.key === 'g' || e.key === 'G') && hasSheet) openGoto();
  });

  // ---- Speaker notes ---------------------------------------------------------------------------------------------
  let notes = null; // [{index,title,note}]
  async function loadNotes() {
    if (!hasNotes) return;
    try {
      const res = await fetch(`/d/${encodeURIComponent(slug)}/notes.json`, { credentials: 'same-origin', cache: 'no-store' });
      if (!res.ok) return;
      notes = await res.json();
      $('notes').hidden = false;
      renderNotes();
    } catch { /* notes stay hidden */ }
  }
  // Minimal, safe markdown: paragraphs, bullet lists, **bold**, *em*, `code`. Everything is text first.
  function renderMarkdown(md) {
    const esc = (s) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const inline = (s) => esc(s)
      .replace(/`([^`]+)`/g, '<code>$1</code>')
      .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
      .replace(/(^|\W)\*([^*]+)\*(?=\W|$)/g, '$1<em>$2</em>');
    const out = [];
    let list = null;
    for (const raw of String(md).split(/\r?\n/)) {
      const line = raw.trim();
      const bullet = /^[-*+]\s+(.*)$/.exec(line);
      if (bullet) { if (!list) { list = []; } list.push(`<li>${inline(bullet[1])}</li>`); continue; }
      if (list) { out.push(`<ul>${list.join('')}</ul>`); list = null; }
      if (line) out.push(`<p>${inline(line)}</p>`);
    }
    if (list) out.push(`<ul>${list.join('')}</ul>`);
    return out.join('');
  }
  function renderNotes() {
    if (!notes) return;
    const cur = notes.find((n) => n.index === current);
    const next = notes.find((n) => n.index === current + 1);
    $('note-current').innerHTML = cur && cur.note ? renderMarkdown(cur.note) : '';
    $('note-next-title').textContent = next ? (next.title ? `${next.index}. ${next.title}` : `Slide ${next.index}`) : 'End of deck';
    $('note-next').innerHTML = next && next.note ? renderMarkdown(next.note).slice(0, 4000) : '';
  }

  // ---- Go-to grid from the slide sheet -------------------------------------------------------------------------
  let sheet = null; // {count, cols, rows, cellWidth, cellHeight}
  async function loadSheet() {
    if (!hasSheet) return;
    try {
      const res = await fetch(`/d/${encodeURIComponent(slug)}/slides.json?v=${encodeURIComponent(build)}`, { credentials: 'same-origin' });
      if (!res.ok) return;
      sheet = await res.json();
      $('goto').hidden = false;
      buildGrid();
    } catch { /* no grid */ }
  }
  function buildGrid() {
    const grid = $('goto-grid');
    grid.innerHTML = '';
    const imgUrl = `/d/${encodeURIComponent(slug)}/slides.jpg?v=${encodeURIComponent(build)}`;
    for (let i = 0; i < sheet.count; i++) {
      const b = document.createElement('button');
      b.type = 'button';
      b.dataset.page = String(i + 1);
      const thumb = document.createElement('div');
      thumb.className = 'thumb';
      const col = i % sheet.cols, row = Math.floor(i / sheet.cols);
      // Background-size scales the whole sheet to the cell width; position selects the tile.
      thumb.style.setProperty('--sheet-w', `${sheet.cols * 100}%`);
      thumb.style.backgroundImage = `url("${imgUrl}")`;
      thumb.style.backgroundPosition = `${sheet.cols > 1 ? (col / (sheet.cols - 1)) * 100 : 0}% ${sheet.rows > 1 ? (row / (sheet.rows - 1)) * 100 : 0}%`;
      const n = document.createElement('span'); n.className = 'n'; n.textContent = String(i + 1);
      b.append(thumb, n);
      b.addEventListener('click', () => { nav('go', { page: i + 1 }); $('goto-dialog').close(); });
      grid.appendChild(b);
    }
    markCurrentInGrid();
  }
  function markCurrentInGrid() {
    document.querySelectorAll('.goto-grid button').forEach((b) => b.classList.toggle('current', Number(b.dataset.page) === current));
  }
  function openGoto() { const d = $('goto-dialog'); if (!d.open) { d.showModal(); d.querySelector('button.current')?.scrollIntoView({ block: 'center' }); } }
  $('goto').addEventListener('click', openGoto);
  $('goto-close').addEventListener('click', () => $('goto-dialog').close());

  // ---- Blackout / message ---------------------------------------------------------------------------------------
  function toggleBlack() {
    const on = $('black').getAttribute('aria-pressed') !== 'true';
    if (send({ t: 'screen', mode: on ? 'black' : 'none' })) $('black').setAttribute('aria-pressed', String(on));
  }
  $('black').addEventListener('click', toggleBlack);
  $('message').addEventListener('click', () => $('message-dialog').showModal());
  $('message-dialog').addEventListener('close', () => {
    const v = $('message-dialog').returnValue;
    if (v === 'show') { const text = $('message-text').value.trim().slice(0, 300); if (text) { send({ t: 'screen', mode: 'message', text }); $('black').setAttribute('aria-pressed', 'false'); } }
    else if (v === 'clear') { send({ t: 'screen', mode: 'none' }); $('black').setAttribute('aria-pressed', 'false'); }
  });

  // ---- Timer + planned-duration countdown -----------------------------------------------------------------------
  let startedAt = 0, accumulated = 0, ticking = null, plannedMs = 0;
  const timerEl = $('timer'), toggle = $('timer-toggle'), countdown = $('countdown');
  const fmt = (ms) => { const s = Math.floor(Math.abs(ms) / 1000); return `${ms < 0 ? '-' : ''}${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; };
  function render() {
    const ms = accumulated + (startedAt ? Date.now() - startedAt : 0);
    timerEl.textContent = fmt(ms);
    if (plannedMs > 0) {
      const left = plannedMs - ms;
      countdown.hidden = false;
      countdown.textContent = left >= 0 ? `${fmt(left)} left` : `${fmt(left)} over`;
      countdown.className = 'countdown' + (left < 0 ? ' over' : left < plannedMs * 0.15 ? ' warn' : '');
    } else countdown.hidden = true;
  }
  toggle.addEventListener('click', () => {
    if (startedAt) { accumulated += Date.now() - startedAt; startedAt = 0; clearInterval(ticking); ticking = null; toggle.textContent = '▶ Timer'; timerEl.classList.remove('running'); }
    else { startedAt = Date.now(); ticking = setInterval(render, 500); toggle.textContent = '⏸ Timer'; timerEl.classList.add('running'); }
    render();
  });
  $('timer-reset').addEventListener('click', () => { accumulated = 0; if (startedAt) startedAt = Date.now(); render(); });
  $('plan').addEventListener('click', () => { $('plan-minutes').value = plannedMs ? String(plannedMs / 60000) : ''; $('plan-dialog').showModal(); });
  $('plan-dialog').addEventListener('close', () => {
    const v = $('plan-dialog').returnValue;
    if (v === 'set') { const m = Number($('plan-minutes').value); plannedMs = m > 0 && m <= 600 ? m * 60000 : 0; }
    else if (v === 'clear') plannedMs = 0;
    try { localStorage.setItem(`podium-plan-${slug}`, String(plannedMs)); } catch {}
    render();
  });
  try { plannedMs = Number(localStorage.getItem(`podium-plan-${slug}`)) || 0; } catch {}
  render();

  // Keep the screen awake while presenting, when the browser allows it.
  let wakeLock = null;
  async function keepAwake() { try { if ('wakeLock' in navigator) wakeLock = await navigator.wakeLock.request('screen'); } catch {} }
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') keepAwake(); });
  keepAwake();
  window.addEventListener('pagehide', () => { closed = true; socket?.close(); });
  connect();
  loadNotes();
  loadSheet();
})();
