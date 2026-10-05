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

  let deckWindows = 0;
  function showPresence(presenters, viewers) {
    if (typeof viewers !== 'number') return;
    // This remote is itself a presenter socket; the other presenting instances are the deck windows it drives.
    deckWindows = Math.max(0, (presenters || 1) - 1);
    presence.textContent = `${viewers} watching${deckWindows ? ` · ${deckWindows} deck window${deckWindows === 1 ? '' : 's'}` : ''}`;
    if (!deckWindows && !current) clicks.textContent = 'no deck window connected: open the deck on the presenting machine';
    else if (!deckWindows) clicks.textContent = 'deck window disconnected';
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

  // ---- Stopwatch (independent of any session) ------------------------------------------------------------------
  let startedAt = 0, accumulated = 0, ticking = null;
  const timerEl = $('timer'), toggle = $('timer-toggle');
  const fmt = (ms) => { const s = Math.floor(Math.abs(ms) / 1000); return `${ms < 0 ? '-' : ''}${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; };
  const fmtLong = (ms) => { const s = Math.floor(Math.abs(ms) / 1000); const h = Math.floor(s / 3600); return `${ms < 0 ? '-' : ''}${h ? h + ':' : ''}${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; };
  function renderStopwatch() { timerEl.textContent = fmt(accumulated + (startedAt ? Date.now() - startedAt : 0)); }
  toggle.addEventListener('click', () => {
    if (startedAt) { accumulated += Date.now() - startedAt; startedAt = 0; clearInterval(ticking); ticking = null; toggle.textContent = '▶ Stopwatch'; timerEl.classList.remove('running'); }
    else { startedAt = Date.now(); ticking = setInterval(renderStopwatch, 500); toggle.textContent = '⏸ Stopwatch'; timerEl.classList.add('running'); }
    renderStopwatch();
  });
  $('timer-reset').addEventListener('click', () => { accumulated = 0; if (startedAt) startedAt = Date.now(); renderStopwatch(); });

  // ---- Live session: countdown from the server's clock, join code, Go live / Adjust / End (owner) ----------------
  const isOwner = document.body.dataset.owner === '1';
  const sessionEl = $('session');
  let session = null;          // { live, startedAt, plannedMinutes, joinCode, joinUrl, ... }
  let clockOffset = 0;         // serverTime - Date.now() at fetch, so phones with a wrong clock still count right
  let sessionTick = null;
  async function ownerApi(method, path, body) {
    const res = await fetch(path, { method, credentials: 'same-origin', headers: { 'x-podium-request': '1', ...(body !== undefined ? { 'content-type': 'application/json' } : {}) }, body: body !== undefined ? JSON.stringify(body) : undefined });
    if (!res.ok) { let msg = `HTTP ${res.status}`; try { msg = (await res.json()).error || msg; } catch {} throw new Error(msg); }
    return res.status === 204 ? null : res.json();
  }
  async function loadSession() {
    try {
      const res = await fetch(`/d/${encodeURIComponent(slug)}/session.json`, { credentials: 'same-origin', cache: 'no-store' });
      if (!res.ok) { sessionEl.hidden = true; return; }
      session = await res.json();
      clockOffset = new Date(session.serverTime).getTime() - Date.now();
      renderSession();
    } catch { /* keep the previous state */ }
  }
  function remainingMs() {
    if (!session || !session.live) return null;
    const elapsed = Date.now() + clockOffset - new Date(session.startedAt).getTime();
    return session.plannedMinutes ? session.plannedMinutes * 60000 - elapsed : -elapsed; // no plan: negative = elapsed
  }
  function renderSession() {
    sessionEl.hidden = false;
    sessionEl.innerHTML = '';
    if (!session || !session.live) {
      if (isOwner) {
        const b = el('button', 'small primary', '● Go live'); b.id = 'go-live';
        b.addEventListener('click', () => { $('golive-minutes').value = localStorage.getItem(`podium-plan-${slug}`) || '45'; $('golive-dialog').showModal(); });
        const hint = el('span', 'faint', 'countdown + join code for the room');
        sessionEl.append(b, hint);
      } else sessionEl.append(el('span', 'faint', 'Not live'));
      clearInterval(sessionTick); sessionTick = null;
      return;
    }
    const big = el('div', 'countdown-big'); big.id = 'countdown-big';
    const meta = el('div', 'session-meta');
    const left = el('span'); left.id = 'countdown-meta';
    meta.append(left);
    if (isOwner) {
      const adjust = el('button', 'small', 'Adjust'); adjust.addEventListener('click', () => { $('plan-minutes').value = session.plannedMinutes || ''; $('plan-dialog').showModal(); });
      const end = el('button', 'small danger', 'End'); end.addEventListener('click', endSession);
      meta.append(adjust, end);
    }
    const join = document.createElement('details'); join.className = 'join';
    const summary = el('summary', '', `Join: ${session.joinUrl.replace(/^https?:\/\//, '')}`);
    const code = el('div', 'join-code', session.joinCode);
    const qr = document.createElement('img'); qr.className = 'join-qr'; qr.alt = 'QR code of the join link'; qr.src = `/d/${encodeURIComponent(slug)}/qr.svg?join=1&v=${encodeURIComponent(session.id)}`;
    const actions = el('div', 'row');
    const share = el('button', 'small', 'Share…'); share.addEventListener('click', () => { if (navigator.share) navigator.share({ title: document.title, text: `Follow along: ${session.joinUrl}`, url: session.joinUrl }).catch(() => {}); else copyText(session.joinUrl); });
    const copy = el('button', 'small', 'Copy link'); copy.addEventListener('click', () => copyText(session.joinUrl));
    actions.append(share, copy);
    join.append(summary, code, qr, actions);
    sessionEl.append(big, meta, join);
    tickSession();
    clearInterval(sessionTick); sessionTick = setInterval(tickSession, 1000);
  }
  function tickSession() {
    const big = $('countdown-big'), meta = $('countdown-meta');
    if (!big || !session || !session.live) return;
    const r = remainingMs();
    if (session.plannedMinutes) {
      big.textContent = fmtLong(r);
      big.className = 'countdown-big' + (r < 0 ? ' over' : r < session.plannedMinutes * 60000 * 0.15 ? ' warn' : '');
      meta.textContent = r >= 0 ? `left of ${session.plannedMinutes} min` : `over the planned ${session.plannedMinutes} min`;
    } else { big.textContent = fmtLong(-r); big.className = 'countdown-big'; meta.textContent = 'elapsed · no planned length'; }
  }
  async function copyText(text) { try { await navigator.clipboard.writeText(text); flash('Copied'); } catch { flash('Copy failed'); } }
  function flash(text) { setConn(text, 'ok'); setTimeout(() => setConn(socket && socket.readyState === WebSocket.OPEN ? 'connected' : 'disconnected', socket && socket.readyState === WebSocket.OPEN ? 'ok' : 'bad'), 1500); }
  function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; }
  $('golive-dialog').addEventListener('close', async () => {
    if ($('golive-dialog').returnValue !== 'start') return;
    const minutes = Number($('golive-minutes').value) || null;
    try {
      await ownerApi('POST', `/api/decks/${encodeURIComponent(slug)}/sessions`, { plannedMinutes: minutes, holdDeploys: $('golive-hold').checked, freeze: $('golive-freeze').checked });
      if (minutes) try { localStorage.setItem(`podium-plan-${slug}`, String(minutes)); } catch {}
      await loadSession();
      if (session && session.live) { const d = sessionEl.querySelector('details'); if (d) d.open = true; }
      if (navigator.vibrate) navigator.vibrate([20, 40, 20]);
    } catch (e) { flash(e.message); }
  });
  $('plan-dialog').addEventListener('close', async () => {
    if ($('plan-dialog').returnValue !== 'set') return;
    const minutes = Number($('plan-minutes').value);
    if (!minutes) return;
    try { await ownerApi('POST', `/api/decks/${encodeURIComponent(slug)}/sessions/plan?minutes=${minutes}`); await loadSession(); } catch (e) { flash(e.message); }
  });
  async function endSession() {
    if (!confirm('End the live session? The join link stops working.')) return;
    try { await ownerApi('POST', `/api/decks/${encodeURIComponent(slug)}/sessions/end?unfreeze=true`); await loadSession(); } catch (e) { flash(e.message); }
  }
  setInterval(loadSession, 30000);
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') loadSession(); });
  // Keep the screen awake while presenting, when the browser allows it.
  let wakeLock = null;
  async function keepAwake() { try { if ('wakeLock' in navigator) wakeLock = await navigator.wakeLock.request('screen'); } catch {} }
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') keepAwake(); });
  keepAwake();
  window.addEventListener('pagehide', () => { closed = true; socket?.close(); });
  connect();
  loadNotes();
  loadSheet();
  loadSession();
})();
