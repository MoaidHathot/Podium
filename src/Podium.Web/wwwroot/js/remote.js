// Podium phone remote (v3): sends navigation, laser pointer, timer and screen commands over the deck's sync socket
// and shows the position reported by the presenting deck windows. Only presenters reach this page (server-enforced)
// and only their sockets may publish. Works for every deck kind Podium can drive (Slidev, presenterm, PowerPoint/PDF).
//   - current and next slide thumbnails (from the slide sheet); the current one doubles as the laser pad
//   - speaker notes (current + next) with adjustable text size; agenda strip with on-pace indicator
//   - live session: Go live / Adjust / End, server-clock countdown, join code + QR, haptic time alerts, recap toast
//   - one timer control: the deck's shared timer when a deck window is connected, else a persisted local stopwatch
//   - swipe on the pad, lock screen for the lectern, wake lock, keyboard shortcuts
(function () {
  'use strict';
  const body = document.body;
  const slug = body.dataset.slug;
  const hasNotes = body.dataset.hasNotes === '1';
  const hasSheet = body.dataset.hasSheet === '1';
  const build = body.dataset.build || '';
  const isOwner = body.dataset.owner === '1';
  const knownTotal = Number(body.dataset.total) || 0;
  const $ = (id) => document.getElementById(id);
  const conn = $('conn'), pageEl = $('page'), totalEl = $('total'), clicksEl = $('clicks'), presenceEl = $('presence');
  let socket = null, backoff = 1000, canSend = false, closed = false;
  let current = 0, totalPages = knownTotal, windows = 0, viewers = 0;
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/sync/${encodeURIComponent(slug)}`;
  const store = { get(k, d) { try { const v = localStorage.getItem(k); return v === null ? d : JSON.parse(v); } catch { return d; } }, set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* private mode */ } } };
  const vibrate = (p) => { if (navigator.vibrate) try { navigator.vibrate(p); } catch { /* unsupported */ } };

  function setConn(text, cls) { conn.textContent = text; conn.className = 'conn ' + (cls || ''); }
  if (totalPages) totalEl.textContent = String(totalPages);

  // ---- Socket -------------------------------------------------------------------------------------------------
  function connect() {
    if (closed) return;
    try { socket = new WebSocket(url); } catch { return retry(); }
    socket.addEventListener('open', () => { backoff = 1000; setConn('connected', 'ok'); });
    socket.addEventListener('message', (ev) => {
      let m; try { m = JSON.parse(ev.data); } catch { return; }
      switch (m.t) {
        case 'hello':
          canSend = !!m.canSend;
          if (!canSend) setConn('view only', 'bad');
          else socket.send(JSON.stringify({ t: 'hi', role: 'remote' }));
          showPresence(m);
          return;
        case 'presence': showPresence(m); return;
        case 'info': show(m.page, m.total, m.clicks, m.clicksTotal); return;
        case 'state':
          if (m.state && typeof m.state.page === 'number' && m.channel !== 'podium - pointer') show(m.state.page, null, m.state.clicks, m.state.clicksTotal);
          if (m.state && m.state.timer && typeof m.state.timer === 'object' && /- shared$/.test(String(m.channel || ''))) onSharedTimer(m.state.timer);
          return;
        case 'screen': $('black').setAttribute('aria-pressed', String(m.mode === 'black')); return;
        case 'session': onSessionMessage(m); return;
      }
    });
    socket.addEventListener('close', (ev) => { socket = null; setConn('disconnected', 'bad'); if (ev.code !== 4403 && ev.code !== 4404) retry(); });
    socket.addEventListener('error', () => {});
  }
  function retry() { if (closed) return; setTimeout(connect, backoff); backoff = Math.min(backoff * 2, 15000); }

  function showPresence(m) {
    if (typeof m.viewers !== 'number') return;
    viewers = m.viewers;
    // Deck windows are the presenting instances this remote drives; a remote alone can move nothing.
    windows = typeof m.windows === 'number' ? m.windows : Math.max(0, (m.presenters || 1) - 1);
    presenceEl.textContent = `${viewers} watching${windows ? ` · ${windows} deck window${windows === 1 ? '' : 's'}` : ''}`;
    presenceEl.classList.toggle('nowindow', !windows);
    if (!windows) clicksEl.textContent = current ? 'deck window disconnected' : 'open the deck on the presenting machine';
    else if (!current) clicksEl.textContent = '';
    renderTimerScope();
  }

  function show(p, t, c, ct) {
    if (typeof p === 'number') { current = p; pageEl.textContent = String(p); }
    if (typeof t === 'number' && t > 0) { totalPages = t; totalEl.textContent = String(t); }
    clicksEl.textContent = typeof ct === 'number' && ct > 0 ? `click ${c ?? 0} / ${ct}` : '';
    renderNotes();
    renderThumbs();
    markCurrentInGrid();
    renderAgenda();
    renderLocked();
  }

  function send(msg) {
    if (!socket || socket.readyState !== WebSocket.OPEN || !canSend) return false;
    socket.send(JSON.stringify(msg));
    return true;
  }
  function nav(action, extra) {
    if (send(Object.assign({ t: 'nav', action }, extra || {}))) vibrate(10);
  }

  $('next').addEventListener('click', () => nav('next'));
  $('prev').addEventListener('click', () => nav('prev'));
  $('first').addEventListener('click', () => nav('first'));
  $('stage-next').addEventListener('click', () => nav('next'));
  document.addEventListener('keydown', (e) => {
    if (document.querySelector('dialog[open]') || !$('locked').hidden) return;
    const t = e.target; if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA')) return;
    if (e.key === 'ArrowRight' || e.key === ' ' || e.key === 'PageDown') { e.preventDefault(); nav('next'); }
    else if (e.key === 'ArrowLeft' || e.key === 'PageUp') { e.preventDefault(); nav('prev'); }
    else if (e.key === 'Home') nav('first');
    else if (e.key === 'End') nav('last');
    else if (e.key === 'b' || e.key === 'B') toggleBlack();
    else if ((e.key === 'g' || e.key === 'G') && hasSheet) openGoto();
    else if (e.key === 'l' || e.key === 'L') toggleLaser();
    else if (e.key === 't' || e.key === 'T') toggleTimer();
  });
  // Swipe on the pad (and on the stage when the laser is off): left = next, right = previous.
  function swipeable(el, onSwipe) {
    let x0 = null, y0 = null, t0 = 0;
    el.addEventListener('touchstart', (e) => { if (e.touches.length === 1) { x0 = e.touches[0].clientX; y0 = e.touches[0].clientY; t0 = Date.now(); } }, { passive: true });
    el.addEventListener('touchend', (e) => {
      if (x0 === null) return;
      const dx = e.changedTouches[0].clientX - x0, dy = e.changedTouches[0].clientY - y0;
      x0 = y0 = null;
      if (Date.now() - t0 < 600 && Math.abs(dx) > 48 && Math.abs(dx) > Math.abs(dy) * 1.5) onSwipe(dx < 0 ? 'next' : 'prev');
    }, { passive: true });
  }
  swipeable($('pad'), (dir) => nav(dir));
  swipeable($('stage-current'), (dir) => { if (!laserOn) nav(dir); });

  // ---- Thumbnails (current = laser pad, next = preview) ------------------------------------------------------
  let sheet = null; // {count, cols, rows, cellWidth, cellHeight}
  const sheetUrl = `/d/${encodeURIComponent(slug)}/slides.jpg?v=${encodeURIComponent(build)}`;
  function tile(el, index) {
    if (!sheet || index < 1 || index > sheet.count) { el.hidden = true; return false; }
    const i = index - 1, col = i % sheet.cols, row = Math.floor(i / sheet.cols);
    el.hidden = false;
    el.style.setProperty('--sheet-w', `${sheet.cols * 100}%`);
    el.style.backgroundImage = `url("${sheetUrl}")`;
    el.style.backgroundPosition = `${sheet.cols > 1 ? (col / (sheet.cols - 1)) * 100 : 0}% ${sheet.rows > 1 ? (row / (sheet.rows - 1)) * 100 : 0}%`;
    return true;
  }
  function renderThumbs() {
    const cur = $('stage-current'), nextBtn = $('stage-next');
    const ok = tile($('thumb-current'), current);
    cur.classList.toggle('no-thumb', !ok);
    const hasNext = current > 0 && current < (totalPages || sheet?.count || 0);
    tile($('thumb-next'), hasNext ? current + 1 : 0);
    nextBtn.classList.toggle('end', !hasNext);
    nextBtn.querySelector('.next-label').textContent = hasNext ? 'Next' : current > 0 ? 'End' : 'Next';
    nextBtn.disabled = !hasNext;
  }
  async function loadSheet() {
    if (!hasSheet) { renderThumbs(); return; }
    try {
      const res = await fetch(`/d/${encodeURIComponent(slug)}/slides.json?v=${encodeURIComponent(build)}`, { credentials: 'same-origin' });
      if (!res.ok) return;
      sheet = await res.json();
      $('goto').hidden = false;
      buildGrid();
      renderThumbs();
    } catch { /* no grid, no thumbnails */ }
  }

  // ---- Laser pointer: drag on the current slide -> {t:'pointer'} at <= 20 messages/s --------------------------
  let laserOn = false, laserActive = false, lastPointerAt = 0, pendingPointer = null, pointerTimer = null;
  const stage = $('stage'), stageCurrent = $('stage-current'), laserDot = $('laser-dot'), laserBtn = $('laser');
  function toggleLaser(force) {
    laserOn = typeof force === 'boolean' ? force : !laserOn;
    stage.classList.toggle('laser-on', laserOn);
    laserBtn.setAttribute('aria-pressed', String(laserOn));
    $('laser-hint').hidden = !laserOn;
    if (!laserOn) clearPointer();
    vibrate(8);
  }
  laserBtn.addEventListener('click', () => toggleLaser());
  function pointerPercent(e) {
    const r = stageCurrent.getBoundingClientRect();
    const x = Math.min(100, Math.max(0, ((e.clientX - r.left) / r.width) * 100));
    const y = Math.min(100, Math.max(0, ((e.clientY - r.top) / r.height) * 100));
    return { x: Math.round(x * 10) / 10, y: Math.round(y * 10) / 10 };
  }
  function sendPointer(p) {
    const now = Date.now();
    if (now - lastPointerAt >= 50) { lastPointerAt = now; send({ t: 'pointer', x: p.x, y: p.y }); pendingPointer = null; }
    else { pendingPointer = p; if (!pointerTimer) pointerTimer = setTimeout(() => { pointerTimer = null; if (pendingPointer) sendPointer(pendingPointer); }, 50 - (now - lastPointerAt)); }
  }
  function clearPointer() {
    laserActive = false; pendingPointer = null;
    if (pointerTimer) { clearTimeout(pointerTimer); pointerTimer = null; }
    laserDot.hidden = true;
    send({ t: 'pointer', x: null, y: null });
  }
  stageCurrent.addEventListener('pointerdown', (e) => {
    if (!laserOn || e.button > 0) return;
    laserActive = true;
    stageCurrent.setPointerCapture(e.pointerId);
    const p = pointerPercent(e);
    laserDot.hidden = false; laserDot.style.left = p.x + '%'; laserDot.style.top = p.y + '%';
    lastPointerAt = 0; sendPointer(p);
    vibrate(5);
    e.preventDefault();
  });
  stageCurrent.addEventListener('pointermove', (e) => {
    if (!laserActive) return;
    const p = pointerPercent(e);
    laserDot.style.left = p.x + '%'; laserDot.style.top = p.y + '%';
    sendPointer(p);
  });
  ['pointerup', 'pointercancel', 'lostpointercapture'].forEach((n) => stageCurrent.addEventListener(n, () => { if (laserActive) clearPointer(); }));

  // ---- Speaker notes -------------------------------------------------------------------------------------------
  let notes = null; // [{index,title,note}]
  let titles = []; // index -> title (from notes or text)
  // Titles from older builds may still carry the heading's inline HTML; the remote shows plain text only.
  const plainTitle = (t) => String(t || '').replace(/<[^>]+>/g, '').replace(/&(nbsp|amp|lt|gt|quot);/g, (m, e) => ({ nbsp: ' ', amp: '&', lt: '<', gt: '>', quot: '"' }[e])).replace(/\s+/g, ' ').trim();
  async function loadNotes() {
    const sources = [];
    if (hasNotes) sources.push(fetch(`/d/${encodeURIComponent(slug)}/notes.json`, { credentials: 'same-origin', cache: 'no-store' }).then((r) => (r.ok ? r.json() : null)).catch(() => null));
    const [n] = await Promise.all(sources);
    if (Array.isArray(n)) {
      notes = n.map((x) => (x && typeof x === 'object' ? { index: x.index, title: plainTitle(x.title), note: x.note } : x));
      $('notes').hidden = false;
      notes.forEach((x) => { if (x && typeof x.index === 'number') titles[x.index] = x.title || ''; });
      if (!totalPages && notes.length) { totalPages = notes.length; totalEl.textContent = String(totalPages); }
      $('agenda').innerHTML = ''; // rebuild with titles
      renderNotes();
      renderThumbs();
      renderAgenda();
    }
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
  // Text size, remembered per browser.
  const SIZES = [0.85, 1, 1.15, 1.3, 1.5];
  let sizeIdx = Math.max(0, Math.min(SIZES.length - 1, store.get('podium-notes-size', 1)));
  function applySize() { document.documentElement.style.setProperty('--notes-size', `${SIZES[sizeIdx]}rem`); store.set('podium-notes-size', sizeIdx); }
  $('notes-smaller').addEventListener('click', () => { sizeIdx = Math.max(0, sizeIdx - 1); applySize(); });
  $('notes-larger').addEventListener('click', () => { sizeIdx = Math.min(SIZES.length - 1, sizeIdx + 1); applySize(); });
  applySize();

  // ---- Agenda strip + pace -------------------------------------------------------------------------------------
  function renderAgenda() {
    const n = totalPages || (sheet && sheet.count) || 0;
    const agenda = $('agenda');
    if (!n || n > 400) { agenda.hidden = true; return; }
    agenda.hidden = false;
    if (agenda.childElementCount !== n) {
      agenda.innerHTML = '';
      for (let i = 1; i <= n; i++) {
        const b = document.createElement('button');
        b.type = 'button'; b.dataset.page = String(i);
        const num = document.createElement('span'); num.className = 'n'; num.textContent = String(i);
        b.append(num, document.createTextNode(titles[i] || ''));
        b.title = titles[i] ? `${i}. ${titles[i]}` : `Slide ${i}`;
        b.addEventListener('click', () => nav('go', { page: i }));
        agenda.appendChild(b);
      }
    }
    for (const b of agenda.children) {
      const p = Number(b.dataset.page);
      b.classList.toggle('current', p === current);
      b.classList.toggle('done', p < current);
    }
    const cur = agenda.querySelector('.current');
    if (cur) cur.scrollIntoView({ inline: 'center', block: 'nearest', behavior: 'smooth' });
  }
  function paceLabel() {
    if (!session || !session.live || !session.plannedMinutes || !totalPages || !current) return null;
    const elapsed = Date.now() + clockOffset - new Date(session.startedAt).getTime();
    const expected = 1 + Math.floor((elapsed / (session.plannedMinutes * 60000)) * totalPages);
    const diff = current - Math.min(totalPages, Math.max(1, expected));
    if (Math.abs(diff) <= 1) return { text: 'on pace', cls: '' };
    return diff < 0 ? { text: `${-diff} slides behind`, cls: 'behind' } : { text: `${diff} slides ahead`, cls: 'ahead' };
  }

  // ---- Go-to grid from the slide sheet -------------------------------------------------------------------------
  function buildGrid() {
    const grid = $('goto-grid');
    grid.innerHTML = '';
    for (let i = 0; i < sheet.count; i++) {
      const b = document.createElement('button');
      b.type = 'button';
      b.dataset.page = String(i + 1);
      const thumb = document.createElement('div');
      thumb.className = 'thumb';
      tile(thumb, i + 1);
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
    if (send({ t: 'screen', mode: on ? 'black' : 'none' })) { $('black').setAttribute('aria-pressed', String(on)); vibrate(on ? [15, 30, 15] : 10); }
  }
  $('black').addEventListener('click', toggleBlack);
  $('message').addEventListener('click', () => $('message-dialog').showModal());
  $('message-dialog').addEventListener('close', () => {
    const v = $('message-dialog').returnValue;
    if (v === 'show') { const text = $('message-text').value.trim().slice(0, 300); if (text) { send({ t: 'screen', mode: 'message', text }); $('black').setAttribute('aria-pressed', 'false'); } }
    else if (v === 'clear') { send({ t: 'screen', mode: 'none' }); $('black').setAttribute('aria-pressed', 'false'); }
  });

  // ---- Timer: the deck's shared timer when a deck window is connected, else a persisted local stopwatch -------
  const timerEl = $('timer'), timerToggle = $('timer-toggle'), timerScope = $('timer-scope');
  const fmt = (ms) => { const s = Math.floor(Math.abs(ms) / 1000); return `${ms < 0 ? '-' : ''}${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; };
  const fmtLong = (ms) => { const s = Math.floor(Math.abs(ms) / 1000); const h = Math.floor(s / 3600); return `${ms < 0 ? '-' : ''}${h ? h + ':' : ''}${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; };
  let local = store.get(`podium-stopwatch-${slug}`, { startedAt: 0, accumulated: 0 });
  let shared = null; // Slidev-style {status, startedAt, pausedAt}
  const useShared = () => windows > 0 && shared !== null;
  function onSharedTimer(t) { shared = { status: t.status || 'stopped', startedAt: Number(t.startedAt) || 0, pausedAt: Number(t.pausedAt) || 0 }; renderTimer(); renderTimerScope(); }
  function sharedElapsed() {
    if (!shared || shared.status === 'stopped' || !shared.startedAt) return 0;
    return shared.status === 'paused' ? shared.pausedAt - shared.startedAt : Date.now() - shared.startedAt;
  }
  function localElapsed() { return local.accumulated + (local.startedAt ? Date.now() - local.startedAt : 0); }
  function renderTimer() {
    if (useShared()) {
      timerEl.textContent = fmt(sharedElapsed());
      timerEl.className = 'timer ' + (shared.status === 'running' ? 'running' : shared.status === 'paused' ? 'paused' : '');
      timerToggle.textContent = shared.status === 'running' ? '⏸ Timer' : '▶ Timer';
    } else {
      timerEl.textContent = fmt(localElapsed());
      timerEl.className = 'timer ' + (local.startedAt ? 'running' : local.accumulated ? 'paused' : '');
      timerToggle.textContent = local.startedAt ? '⏸ Timer' : '▶ Timer';
    }
  }
  function renderTimerScope() { timerScope.textContent = useShared() ? 'deck' : windows > 0 ? 'deck' : 'phone'; timerScope.title = useShared() || windows > 0 ? 'The timer shown in the presenter view' : 'Local stopwatch (no deck window connected)'; }
  function toggleTimer() {
    if (windows > 0) { if (send({ t: 'timer', op: 'toggle' })) { vibrate(10); if (shared === null) shared = { status: 'stopped', startedAt: 0, pausedAt: 0 }; } return; }
    if (local.startedAt) { local = { startedAt: 0, accumulated: local.accumulated + (Date.now() - local.startedAt) }; }
    else local = { startedAt: Date.now(), accumulated: local.accumulated };
    store.set(`podium-stopwatch-${slug}`, local);
    renderTimer();
  }
  timerToggle.addEventListener('click', toggleTimer);
  $('timer-reset').addEventListener('click', () => {
    if (windows > 0) { send({ t: 'timer', op: 'reset' }); return; }
    local = { startedAt: local.startedAt ? Date.now() : 0, accumulated: 0 };
    store.set(`podium-stopwatch-${slug}`, local);
    renderTimer();
  });
  setInterval(renderTimer, 500);
  renderTimer();

  // ---- Live session: countdown from the server's clock, join code, Go live / Adjust / End (owner) ----------------
  const sessionEl = $('session');
  let session = null;          // { live, id, startedAt, plannedMinutes, joinCode, joinUrl, ... }
  let clockOffset = 0;         // serverTime - Date.now() at fetch, so phones with a wrong clock still count right
  let sessionTick = null;
  let alerted = {};            // haptic milestones already fired for this session
  async function ownerApi(method, path, body) {
    const res = await fetch(path, { method, credentials: 'same-origin', headers: { 'x-podium-request': '1', ...(body !== undefined ? { 'content-type': 'application/json' } : {}) }, body: body !== undefined ? JSON.stringify(body) : undefined });
    if (!res.ok) { let msg = `HTTP ${res.status}`; try { msg = (await res.json()).error || msg; } catch {} throw new Error(msg); }
    return res.status === 204 ? null : res.json();
  }
  async function loadSession() {
    try {
      const res = await fetch(`/d/${encodeURIComponent(slug)}/session.json`, { credentials: 'same-origin', cache: 'no-store' });
      if (!res.ok) { sessionEl.hidden = true; return; }
      applySession(await res.json());
    } catch { /* keep the previous state */ }
  }
  function applySession(s) {
    const wasLive = !!(session && session.live), sameId = session && s && session.id === s.id;
    session = s;
    if (s && s.serverTime) clockOffset = new Date(s.serverTime).getTime() - Date.now();
    if (!sameId) alerted = {};
    renderSession();
    if (s && s.live && !wasLive && !s.replay) vibrate([20, 40, 20]);
  }
  // Pushed by the server to every presenter socket on start / plan change / end (no polling needed).
  function onSessionMessage(m) {
    if (m.live) { applySession(m); return; }
    const recap = m.recap;
    applySession({ live: false, isOwner, serverTime: m.serverTime });
    if (recap) {
      const mins = Math.max(1, Math.round(recap.durationSeconds / 60));
      showToast(`Session ended · ${mins} min · peak ${recap.peakViewers} watching · ${recap.slidesVisited} slides`, `/decks/${encodeURIComponent(slug)}#sessions`, 'Recap');
      vibrate([30, 60, 30]);
    }
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
        b.addEventListener('click', () => { $('golive-minutes').value = store.get(`podium-plan-${slug}`, 45); $('golive-dialog').showModal(); });
        const hint = el('span', 'faint', 'countdown + join code for the room');
        sessionEl.append(b, hint);
      } else sessionEl.append(el('span', 'faint', 'Not live'));
      clearInterval(sessionTick); sessionTick = null;
      renderLocked();
      return;
    }
    const top = el('div', 'countdown-row');
    const big = el('div', 'countdown-big'); big.id = 'countdown-big';
    const pace = el('span', 'pace'); pace.id = 'pace'; pace.hidden = true;
    top.append(big, pace);
    const meta = el('div', 'session-meta');
    const left = el('span'); left.id = 'countdown-meta';
    meta.append(left);
    if (isOwner) {
      const adjust = el('button', 'small', 'Adjust'); adjust.addEventListener('click', () => { $('plan-minutes').value = session.plannedMinutes || ''; $('plan-dialog').showModal(); });
      const end = el('button', 'small danger', 'End'); end.addEventListener('click', endSession);
      meta.append(adjust, end);
    }
    const join = document.createElement('details'); join.className = 'join';
    const summary = el('summary', '', `Join: ${String(session.joinUrl || '').replace(/^https?:\/\//, '')}`);
    const code = el('div', 'join-code', session.joinCode || '');
    const qr = document.createElement('img'); qr.className = 'join-qr'; qr.alt = 'QR code of the join link'; qr.src = `/d/${encodeURIComponent(slug)}/qr.svg?join=1&v=${encodeURIComponent(session.id || '')}`;
    const actions = el('div', 'row');
    const share = el('button', 'small', 'Share…'); share.addEventListener('click', () => { if (navigator.share) navigator.share({ title: document.title, text: `Follow along: ${session.joinUrl}`, url: session.joinUrl }).catch(() => {}); else copyText(session.joinUrl); });
    const copy = el('button', 'small', 'Copy link'); copy.addEventListener('click', () => copyText(session.joinUrl));
    actions.append(share, copy);
    join.append(summary, code, qr, actions);
    sessionEl.append(top, meta, join);
    tickSession();
    clearInterval(sessionTick); sessionTick = setInterval(tickSession, 1000);
  }
  function tickSession() {
    const big = $('countdown-big'), meta = $('countdown-meta'), pace = $('pace');
    if (!big || !session || !session.live) return;
    const r = remainingMs();
    if (session.plannedMinutes) {
      big.textContent = fmtLong(r);
      big.className = 'countdown-big' + (r < 0 ? ' over' : r < session.plannedMinutes * 60000 * 0.15 ? ' warn' : '');
      meta.textContent = r >= 0 ? `left of ${session.plannedMinutes} min` : `over the planned ${session.plannedMinutes} min`;
      hapticMilestones(r);
    } else { big.textContent = fmtLong(-r); big.className = 'countdown-big'; meta.textContent = 'elapsed · no planned length'; }
    const p = paceLabel();
    pace.hidden = !p;
    if (p) { pace.textContent = p.text; pace.className = 'pace ' + p.cls; }
    renderLocked();
  }
  // A short buzz at five minutes and one minute left, a long one at zero, then every five minutes over.
  function hapticMilestones(r) {
    const fire = (key, pattern) => { if (!alerted[key]) { alerted[key] = true; if (document.visibilityState === 'visible') vibrate(pattern); } };
    if (r <= 5 * 60000 && r > 4 * 60000) fire('m5', [40, 60, 40]);
    if (r <= 60000 && r > 0) fire('m1', [60, 60, 60, 60, 60]);
    if (r <= 0 && r > -60000) fire('m0', [250, 100, 250]);
    if (r < 0) { const over = Math.floor(-r / (5 * 60000)); if (over >= 1) fire('over' + over, [120, 80, 120, 80, 120]); }
  }
  async function copyText(text) { try { await navigator.clipboard.writeText(text); flash('Copied'); } catch { flash('Copy failed'); } }
  function flash(text) { setConn(text, 'ok'); setTimeout(() => setConn(socket && socket.readyState === WebSocket.OPEN ? 'connected' : 'disconnected', socket && socket.readyState === WebSocket.OPEN ? 'ok' : 'bad'), 1500); }
  function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; }
  let toastEl = null;
  function showToast(text, href, linkText) {
    if (toastEl) toastEl.remove();
    toastEl = el('div', 'toast'); toastEl.setAttribute('role', 'status');
    toastEl.append(document.createTextNode(text));
    if (href) { toastEl.append(document.createTextNode(' · ')); const a = el('a', '', linkText || 'Open'); a.href = href; toastEl.append(a); }
    document.body.appendChild(toastEl);
    setTimeout(() => { if (toastEl) { toastEl.remove(); toastEl = null; } }, 12000);
  }
  $('golive-dialog').addEventListener('close', async () => {
    if ($('golive-dialog').returnValue !== 'start') return;
    const minutes = Number($('golive-minutes').value) || null;
    try {
      await ownerApi('POST', `/api/decks/${encodeURIComponent(slug)}/sessions`, { plannedMinutes: minutes, holdDeploys: $('golive-hold').checked, freeze: $('golive-freeze').checked });
      if (minutes) store.set(`podium-plan-${slug}`, minutes);
      await loadSession();
      if (session && session.live) { const d = sessionEl.querySelector('details'); if (d) d.open = true; }
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
  setInterval(loadSession, 60000);
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') loadSession(); });

  // ---- Lock screen -------------------------------------------------------------------------------------------
  const locked = $('locked'), unlockBtn = $('unlock');
  function renderLocked() {
    if (locked.hidden) return;
    const clock = $('locked-clock');
    const r = remainingMs();
    if (r === null) { clock.textContent = fmt(useShared() ? sharedElapsed() : localElapsed()); clock.className = 'locked-clock idle'; }
    else if (session.plannedMinutes) { clock.textContent = fmtLong(r); clock.className = 'locked-clock' + (r < 0 ? ' over' : r < session.plannedMinutes * 60000 * 0.15 ? ' warn' : ''); }
    else { clock.textContent = fmtLong(-r); clock.className = 'locked-clock'; }
    $('locked-pos').textContent = current ? `Slide ${current}${totalPages ? ` of ${totalPages}` : ''}` : '';
    const next = notes && notes.find((n) => n.index === current + 1);
    $('locked-next').textContent = next ? `Next: ${next.title || 'slide ' + next.index}` : current && totalPages && current >= totalPages ? 'Last slide' : '';
  }
  $('lock').addEventListener('click', () => { locked.hidden = false; renderLocked(); vibrate(10); });
  let holdTimer = null;
  const startHold = (e) => { e.preventDefault(); unlockBtn.classList.add('holding'); holdTimer = setTimeout(() => { locked.hidden = true; unlockBtn.classList.remove('holding'); vibrate([10, 30, 10]); }, 700); };
  const cancelHold = () => { unlockBtn.classList.remove('holding'); if (holdTimer) { clearTimeout(holdTimer); holdTimer = null; } };
  unlockBtn.addEventListener('pointerdown', startHold);
  ['pointerup', 'pointercancel', 'pointerleave'].forEach((n) => unlockBtn.addEventListener(n, cancelHold));
  setInterval(renderLocked, 1000);

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
  renderThumbs();
  renderAgenda();
})();
