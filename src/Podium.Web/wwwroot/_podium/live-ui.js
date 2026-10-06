// Podium live UI, injected into every served deck next to live.js. Draws everything the room and the presenter see
// inside a deck window: the live pill (follow / browse freely / jump to live), the presenter HUD in the Slidev
// presenter view (watching count, session countdown, join code and QR, go live / end), the blackout and message
// overlay raised from the phone, the laser dot for decks Slidev does not render itself, toasts, and the "session has
// ended" screen. It talks to the deck through the bridge (window.__podium) set up by the Slidev addon or by the
// server-served adapters (bridge.js + pages.js / presenterm.js), so UI changes never require a deck rebuild.
(function () {
  'use strict';
  var script = document.currentScript || document.querySelector('script[src*="/_podium/live-ui.js"]');
  if (!script) return;
  var cfg = {
    slug: script.getAttribute('data-slug'),
    build: script.getAttribute('data-build'),
    kind: script.getAttribute('data-kind') || 'slidev',
    external: script.getAttribute('data-external') === '1',
    version: script.getAttribute('data-version') || '',
  };
  if (!cfg.slug) return;
  if (/[?&]print=/.test(location.search) || /\/print(\/|$)/.test(location.pathname)) return;
  if (cfg.kind === 'slidev') {
    // Older builds carry their own pill and blackout inside the addon; stay out of their way until they are rebuilt.
    var addon = Number((document.querySelector('meta[name="podium-addon"]') || {}).content || 0);
    if (!(addon >= 3)) return;
  }

  // ---- DOM scaffolding --------------------------------------------------------------------------------------------
  var html = document.documentElement;
  var link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/_podium/live-ui.css' + (cfg.version ? '?v=' + encodeURIComponent(cfg.version) : '');
  document.head.appendChild(link);
  var ui = el('div', { id: 'podium-ui' });
  var toasts = el('div', { id: 'podium-toasts' });
  ui.appendChild(toasts);
  html.appendChild(ui);
  // Slidev mutes its keyboard shortcuts while a button has focus; a presenter who clicked the HUD must still be able
  // to press the arrow keys, so controls give focus back to the deck once they have done their job.
  ui.addEventListener('click', function (e) {
    var b = e.target && e.target.closest ? e.target.closest('button') : null;
    if (b && !b.closest('#podium-pop')) setTimeout(function () { if (document.activeElement === b) b.blur(); }, 0);
  });

  function el(tag, attrs, children) {
    var e = document.createElement(tag);
    if (attrs) Object.keys(attrs).forEach(function (k) {
      if (attrs[k] === null || attrs[k] === undefined || attrs[k] === false) return;
      if (k === 'text') e.textContent = attrs[k];
      else if (k === 'class') e.className = attrs[k];
      else if (k === 'hidden') { if (attrs[k]) e.hidden = true; }
      else e.setAttribute(k, attrs[k]);
    });
    (children || []).forEach(function (c) { if (c) e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return e;
  }
  function toast(text, ms) {
    var t = el('div', { class: 'podium-toast', role: 'status', text: text });
    toasts.appendChild(t);
    setTimeout(function () { t.style.opacity = '0'; t.style.transition = 'opacity .3s'; setTimeout(function () { t.remove(); }, 320); }, ms || 4500);
  }
  function fmtClock(ms) {
    var neg = ms < 0; ms = Math.abs(ms);
    var s = Math.floor(ms / 1000), m = Math.floor(s / 60), h = Math.floor(m / 60);
    var txt = h > 0 ? h + ':' + String(m % 60).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0') : m + ':' + String(s % 60).padStart(2, '0');
    return (neg ? '+' : '') + txt;
  }
  function fmtMinutes(seconds) { var m = Math.round(seconds / 60); return m < 1 ? '<1 min' : m + ' min'; }

  // ---- Boot once the bridge exists ----------------------------------------------------------------------------
  if (window.__podium && window.__podium.protocol >= 3) start(window.__podium);
  else window.addEventListener('podium:bridge', function onBridge() { window.removeEventListener('podium:bridge', onBridge); start(window.__podium); });

  function start(bridge) {
    var presence = { presenters: 0, viewers: 0, windows: 0, remotes: 0 };
    var presenter = { page: null, clicks: 0 };
    var session = null;          // presenter payload when canSend, {live} for viewers
    var clockOffset = 0;         // serverTime - Date.now()
    var screen = { mode: 'none', text: '' };
    var ended = false;
    var isOwner = false;
    var pill = null, pillDot = null, pillLabel = null, pillBtn = null;
    var hud = null, hudParts = null, pop = null, popKind = null;
    var screenEl = null, laser = null, laserTimer = null;

    var isPresenterView = function () { return bridge.role === 'presenter'; };

    // ---- Live pill ----------------------------------------------------------------------------------------------
    function ensurePill() {
      if (pill) return;
      pillDot = el('span', { class: 'podium-dot podium-pulse' });
      pillLabel = el('span');
      pillBtn = el('button', { type: 'button', class: 'podium-btn' });
      pillBtn.addEventListener('click', function () {
        if (bridge.following) bridge.setFollowing(false);
        else { bridge.setFollowing(true); if (presenter.page) bridge.go(presenter.page, presenter.clicks); }
        render();
      });
      pill = el('div', { id: 'podium-live', role: 'status', 'aria-live': 'polite' }, [pillDot, pillLabel, pillBtn]);
      ui.appendChild(pill);
    }

    function render() {
      if (ended) return;
      var connected = bridge.connected;
      // A presenting window shows the pill only when there is something to show (a session, a room, a phone); the
      // owner looking at their own deck alone sees nothing. Viewers see it whenever a deck window is presenting.
      var live = bridge.canSend
        ? (!!(session && session.live) || presence.viewers > 0 || presence.remotes > 0)
        : presence.windows > 0;
      if (isPresenterView()) { if (pill) pill.hidden = true; renderHud(); return; }
      if (!live && !pill) return;
      ensurePill();
      var blackedOut = bridge.canSend && screen.mode !== 'none';
      if (!live || blackedOut) { pill.hidden = true; return; }
      pill.hidden = false;
      pill.classList.toggle('podium-off', !connected);
      pill.classList.toggle('podium-min', bridge.canSend && !!document.fullscreenElement);
      if (!connected) { pillLabel.textContent = 'Reconnecting…'; pillBtn.hidden = true; return; }
      if (bridge.canSend) {
        pillLabel.textContent = 'Live · ' + presence.viewers + ' watching';
        pillBtn.hidden = true;
        return;
      }
      pillBtn.hidden = false;
      if (bridge.following) {
        pillLabel.textContent = 'Live · following';
        pillBtn.textContent = 'Browse freely';
      } else {
        var here = bridge.position().page;
        var behind = presenter.page && presenter.page !== here ? ' · presenter on ' + presenter.page : '';
        pillLabel.textContent = 'Live · browsing' + behind;
        pillBtn.textContent = 'Jump to live';
      }
    }

    // ---- Presenter HUD (Slidev presenter view) ------------------------------------------------------------------
    function ensureHud() {
      if (hud) return;
      hudParts = {
        dot: el('span', { class: 'podium-dot' }),
        state: el('span'),
        watching: el('span', { class: 'podium-muted' }),
        clock: el('span', { class: 'podium-clock', hidden: true }),
        code: el('button', { type: 'button', class: 'podium-code', hidden: true, title: 'Show the join code and QR for the room' }),
        golive: el('button', { type: 'button', class: 'podium-btn podium-primary', text: 'Go live', hidden: true }),
        end: el('button', { type: 'button', class: 'podium-btn podium-danger', text: 'End', hidden: true, title: 'End the live session' }),
        remote: el('button', { type: 'button', class: 'podium-btn', text: 'Remote', hidden: cfg.external, title: 'Open the phone remote: scan the QR with your phone' }),
        screen: el('span', { class: 'podium-muted', hidden: true, text: 'Screen black' }),
      };
      hudParts.code.addEventListener('click', function () { togglePop('join'); });
      hudParts.golive.addEventListener('click', function () { togglePop('golive'); });
      hudParts.remote.addEventListener('click', function () { togglePop('remote'); });
      hudParts.end.addEventListener('click', endSession);
      hud = el('div', { id: 'podium-hud', role: 'status' }, [hudParts.dot, hudParts.state, el('span', { class: 'podium-sep' }), hudParts.watching, hudParts.clock, hudParts.screen, hudParts.code, hudParts.remote, hudParts.golive, hudParts.end]);
      ui.appendChild(hud);
    }
    function renderHud() {
      if (!bridge.canSend) { if (hud) hud.hidden = true; return; }
      ensureHud();
      ensureOwnerInfo();
      hud.hidden = false;
      var live = !!(session && session.live);
      hud.classList.toggle('podium-on', live);
      hudParts.state.textContent = !bridge.connected ? 'Reconnecting…' : live ? 'Live' : 'Not live';
      hudParts.watching.textContent = presence.viewers + ' watching';
      hudParts.clock.hidden = !live;
      hudParts.code.hidden = !(live && session.joinCode);
      if (live && session.joinCode) hudParts.code.textContent = session.joinCode;
      hudParts.golive.hidden = live || !isOwner || cfg.external;
      hudParts.end.hidden = !live || !isOwner || cfg.external;
      hudParts.screen.hidden = screen.mode === 'none';
      hudParts.screen.textContent = screen.mode === 'black' ? 'Screen black' : screen.mode === 'message' ? 'Showing message' : '';
      tickClock();
    }
    var ownerChecked = false;
    // Only the owner may start or end sessions (co-presenters see the HUD without those buttons). Looked up once the
    // presenter view is known to be one: at socket "hello" time the router may not have resolved the route yet.
    function ensureOwnerInfo() {
      if (ownerChecked || cfg.external || !bridge.canSend || !isPresenterView()) return;
      ownerChecked = true;
      fetch('/d/' + encodeURIComponent(cfg.slug) + '/session.json', { credentials: 'same-origin', cache: 'no-store' })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (s) { if (s) { isOwner = !!s.isOwner; if (s.serverTime) clockOffset = new Date(s.serverTime).getTime() - Date.now(); } render(); })
        .catch(function () { ownerChecked = false; });
    }
    function remainingMs() {
      if (!session || !session.live || !session.startedAt) return null;
      var elapsed = Date.now() + clockOffset - new Date(session.startedAt).getTime();
      return session.plannedMinutes ? session.plannedMinutes * 60000 - elapsed : -elapsed;
    }
    function tickClock() {
      if (!hud || hud.hidden || hudParts.clock.hidden) return;
      var ms = remainingMs();
      if (ms === null) { hudParts.clock.textContent = ''; return; }
      var planned = session.plannedMinutes ? session.plannedMinutes * 60000 : 0;
      hudParts.clock.textContent = fmtClock(ms) + (planned ? ' left' : '');
      hudParts.clock.classList.toggle('podium-over', planned > 0 && ms < 0);
      hudParts.clock.classList.toggle('podium-warn', planned > 0 && ms >= 0 && ms < planned * 0.15);
    }
    setInterval(tickClock, 1000);

    function togglePop(kind) {
      if (pop && popKind === kind) { closePop(); return; }
      closePop();
      popKind = kind;
      pop = el('div', { id: 'podium-pop', role: 'dialog', 'aria-label': kind === 'join' ? 'Join this talk' : kind === 'remote' ? 'Phone remote' : kind === 'audience' ? 'From the room' : 'Go live' });
      if (kind === 'remote' && !cfg.external) {
        var remoteUrl = location.origin + '/d/' + encodeURIComponent(cfg.slug) + '/remote';
        pop.appendChild(el('h3', { text: 'Phone remote' }));
        pop.appendChild(el('img', { class: 'podium-qr', alt: 'QR code of the phone remote', src: '/d/' + encodeURIComponent(cfg.slug) + '/qr.svg?remote=1' }));
        pop.appendChild(el('span', { class: 'podium-url', text: remoteUrl.replace(/^https?:\/\//, '') }));
        pop.appendChild(el('p', { text: 'Scan with your phone. You sign in with GitHub there once; the remote then shows your notes, drives the slides, points the laser and runs the countdown.' }));
        var rrow = el('div', { class: 'podium-row' });
        var rclose = el('button', { type: 'button', class: 'podium-btn', text: 'Close' });
        rclose.addEventListener('click', closePop);
        rrow.append(rclose);
        pop.appendChild(rrow);
      } else if (kind === 'join' && session && session.live) {
        pop.appendChild(el('h3', { text: 'Let the room follow along' }));
        if (!cfg.external) pop.appendChild(el('img', { class: 'podium-qr', alt: 'QR code for ' + (session.joinUrl || 'the join link'), src: '/d/' + encodeURIComponent(cfg.slug) + '/qr.svg?join=1&v=' + encodeURIComponent(session.id || '') }));
        pop.appendChild(el('strong', { class: 'podium-bigcode', text: session.joinCode || '' }));
        if (session.joinUrl) pop.appendChild(el('span', { class: 'podium-url', text: session.joinUrl.replace(/^https?:\/\//, '') }));
        pop.appendChild(el('p', { text: 'Anyone with the code follows your slides while the session is live; it stops working when you end it.' }));
        var row = el('div', { class: 'podium-row' });
        var copy = el('button', { type: 'button', class: 'podium-btn', text: 'Copy link' });
        copy.addEventListener('click', function () { if (navigator.clipboard && session.joinUrl) navigator.clipboard.writeText(session.joinUrl).then(function () { copy.textContent = 'Copied'; }, function () {}); });
        var close = el('button', { type: 'button', class: 'podium-btn', text: 'Close' });
        close.addEventListener('click', closePop);
        row.append(copy, close);
        pop.appendChild(row);
      } else if (kind === 'golive') {
        pop.appendChild(el('h3', { text: 'Go live' }));
        pop.appendChild(el('p', { text: 'Freezes the slides, mints a join code for the room and starts the countdown on your phone and here.' }));
        var minutes = el('input', { type: 'number', min: '1', max: '600', value: String(storedPlan()) });
        var freeze = el('input', { type: 'checkbox' }); freeze.checked = true;
        var hold = el('input', { type: 'checkbox' });
        var rehearsal = el('input', { type: 'checkbox' });
        pop.appendChild(el('label', {}, ['Planned length ', minutes, ' minutes']));
        pop.appendChild(el('label', {}, [freeze, ' Freeze the deck while live']));
        pop.appendChild(el('label', {}, [hold, ' Hold deployments while live']));
        pop.appendChild(el('label', {}, [rehearsal, ' This is a rehearsal']));
        var err = el('p', { class: 'podium-error', hidden: true });
        pop.appendChild(err);
        var row2 = el('div', { class: 'podium-row' });
        var cancel = el('button', { type: 'button', class: 'podium-btn', text: 'Cancel' });
        cancel.addEventListener('click', closePop);
        var startBtn = el('button', { type: 'button', class: 'podium-btn podium-primary', text: 'Start' });
        startBtn.addEventListener('click', function () {
          var mins = Math.max(1, Math.min(600, Number(minutes.value) || 45));
          startBtn.disabled = true;
          ownerApi('POST', '/api/decks/' + encodeURIComponent(cfg.slug) + '/sessions', { plannedMinutes: mins, holdDeploys: hold.checked, freeze: freeze.checked, rehearsal: rehearsal.checked })
            .then(function () { try { localStorage.setItem('podium-plan-' + cfg.slug, String(mins)); } catch (e) { /* private mode */ } closePop(); })
            .catch(function (e) { err.hidden = false; err.textContent = e.message || 'Could not start the session'; startBtn.disabled = false; });
        });
        row2.append(cancel, startBtn);
        pop.appendChild(row2);
        setTimeout(function () { minutes.focus(); minutes.select(); }, 0);
      } else if (kind === 'audience') {
        renderAudiencePop();
      } else { pop = null; popKind = null; return; }
      ui.appendChild(pop);
      document.addEventListener('keydown', escClose);
    }
    function escClose(e) { if (e.key === 'Escape') closePop(); }
    function closePop() {
      if (pop) pop.remove();
      pop = null; popKind = null;
      document.removeEventListener('keydown', escClose);
      var a = document.activeElement;
      if (a && a !== document.body && ui.contains(a) && a.blur) a.blur();
    }
    function storedPlan() { try { return Number(localStorage.getItem('podium-plan-' + cfg.slug)) || 45; } catch (e) { return 45; } }
    function endSession() {
      if (!confirm('End the live session? The join code stops working and the room is told the talk is over.')) return;
      ownerApi('POST', '/api/decks/' + encodeURIComponent(cfg.slug) + '/sessions/end?unfreeze=true').catch(function (e) { toast(e.message || 'Could not end the session'); });
    }
    function ownerApi(method, path, body) {
      return fetch(path, { method: method, credentials: 'same-origin', headers: body ? { 'content-type': 'application/json', 'x-podium-request': '1' } : { 'x-podium-request': '1' }, body: body ? JSON.stringify(body) : undefined })
        .then(function (r) {
          if (r.ok) return r.status === 204 ? null : r.json().catch(function () { return null; });
          return r.json().catch(function () { return {}; }).then(function (j) { throw new Error(j && j.error ? j.error : 'Request failed (' + r.status + ')'); });
        });
    }

    // ---- Blackout / message ------------------------------------------------------------------------------------
    function renderScreen() {
      var show = screen.mode !== 'none' && !isPresenterView();
      if (!show) { if (screenEl) { screenEl.remove(); screenEl = null; } render(); return; }
      if (!screenEl) { screenEl = el('div', { id: 'podium-screen', role: 'status', 'aria-live': 'polite' }); ui.appendChild(screenEl); }
      screenEl.textContent = screen.mode === 'message' ? String(screen.text || '').slice(0, 300) : '';
      render();
    }

    // ---- Laser dot for non-Slidev kinds -------------------------------------------------------------------------
    function slideRect() {
      if (cfg.kind === 'pages') { var img = document.querySelector('#podium-pages img.pp-slide'); return img ? img.getBoundingClientRect() : null; }
      if (cfg.kind === 'presenterm') return document.body.getBoundingClientRect();
      return null;
    }
    function renderLaser(x, y) {
      if (cfg.kind === 'slidev') return; // Slidev draws the shared cursor itself
      if (typeof x !== 'number' || typeof y !== 'number') { if (laser) laser.style.opacity = '0'; return; }
      var r = slideRect();
      if (!r) return;
      if (!laser) { laser = el('div', { id: 'podium-laser', 'aria-hidden': 'true' }); ui.appendChild(laser); }
      laser.style.opacity = '1';
      laser.style.left = (r.left + r.width * x / 100) + 'px';
      laser.style.top = (r.top + r.height * y / 100) + 'px';
      clearTimeout(laserTimer);
      laserTimer = setTimeout(function () { if (laser) laser.style.opacity = '0'; }, 8000);
    }

    // ---- Session ended (this viewer's admission died with it) -------------------------------------------------
    function showEnded() {
      if (ended) return;
      ended = true;
      closePop();
      if (pill) pill.hidden = true;
      if (hud) hud.hidden = true;
      if (screenEl) { screenEl.remove(); screenEl = null; }
      ui.appendChild(el('div', { id: 'podium-ended', role: 'alert' }, [
        el('h1', { text: 'This session has ended' }),
        el('p', { text: 'Thanks for following along. The join link no longer opens the slides.' }),
        el('span', { class: 'podium-mark', text: 'Podium' }),
      ]));
    }

    // ---- Audience: reactions, questions, polls --------------------------------------------------------------
    // Viewers get a bar (react / ask / vote), everyone sees floating reactions, the pinned question and shown poll
    // results; the presenter view gets counters and a moderation popover. The server decides what is allowed.
    var EMOJI = { clap: '\uD83D\uDC4F', heart: '\u2764\uFE0F', laugh: '\uD83D\uDE02', think: '\uD83E\uDD14', up: '\uD83D\uDC4D', party: '\uD83C\uDF89' };
    var KINDS = ['clap', 'heart', 'laugh', 'think', 'up', 'party'];
    var aud = { live: false, muted: false, settings: { reactions: false, questions: false, polls: false, floatReactions: false, nicknames: false }, totals: {}, questions: [], poll: null };
    var cid = null, myQuestions = {}, myUpvotes = {}, myVotes = {};
    try {
      cid = localStorage.getItem('podium-cid');
      if (!cid || !/^[A-Za-z0-9_-]{16,40}$/.test(cid)) { cid = Array.from(crypto.getRandomValues(new Uint8Array(20)), function (b) { return 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'[b % 62]; }).join(''); localStorage.setItem('podium-cid', cid); }
      myUpvotes = JSON.parse(localStorage.getItem('podium-upvotes-' + cfg.slug) || '{}'); myVotes = JSON.parse(localStorage.getItem('podium-votes-' + cfg.slug) || '{}');
    } catch (e) { cid = cid || 'anon' + Math.random().toString(36).slice(2, 14) + Math.random().toString(36).slice(2, 8); }
    var bar = null, barParts = null, sheet = null, sheetKind = null, banner = null, resultsEl = null, floatCount = 0;
    var canReact = function () { return aud.live && !aud.muted && aud.settings.reactions; };
    var canAsk = function () { return aud.live && !aud.muted && aud.settings.questions; };
    var pollOpen = function () { return aud.live && aud.settings.polls && aud.poll && (aud.poll.open || aud.poll.shown); };
    var isViewer = function () { return !bridge.canSend; };
    var isProjector = function () { return bridge.canSend && !isPresenterView(); };

    function ensureBar() {
      if (bar) return;
      barParts = { reactions: el('div', { class: 'podium-reactions' }), ask: el('button', { type: 'button', class: 'podium-abtn', text: 'Ask' }), poll: el('button', { type: 'button', class: 'podium-abtn podium-poll-chip', text: 'Poll', hidden: true }), muted: el('span', { class: 'podium-muted-note', text: 'The presenter has muted the room', hidden: true }) };
      KINDS.forEach(function (k) {
        var b = el('button', { type: 'button', class: 'podium-react', 'aria-label': 'React ' + k, text: EMOJI[k], 'data-kind': k });
        b.addEventListener('click', function () { if (!canReact()) return; sendMsg({ t: 'react', kind: k }); ownPending[k] = (ownPending[k] || 0) + 1; floatEmoji(k, 1, true); b.classList.remove('podium-pop'); void b.offsetWidth; b.classList.add('podium-pop'); });
        barParts.reactions.appendChild(b);
      });
      barParts.ask.addEventListener('click', function () { toggleSheet('ask'); });
      barParts.poll.addEventListener('click', function () { toggleSheet('poll'); });
      bar = el('div', { id: 'podium-audience', role: 'toolbar', 'aria-label': 'Audience' }, [barParts.reactions, barParts.ask, barParts.poll, barParts.muted]);
      ui.appendChild(bar);
    }
    function renderAudience() {
      if (ended) return;
      var show = isViewer() && aud.live && aud.settings && (aud.settings.reactions || aud.settings.questions || aud.settings.polls);
      if (!show) { if (bar) bar.hidden = true; ui.classList.remove('podium-has-bar'); if (sheet) closeSheet(); renderBanner(); renderResults(); return; }
      ensureBar();
      bar.hidden = false;
      ui.classList.add('podium-has-bar');
      barParts.reactions.hidden = !aud.settings.reactions || aud.muted;
      barParts.ask.hidden = !aud.settings.questions || aud.muted;
      barParts.ask.textContent = aud.questions.length ? 'Q&A \u00B7 ' + aud.questions.length : 'Ask';
      barParts.poll.hidden = !pollOpen();
      barParts.poll.textContent = aud.poll && myVotes[aud.poll.id] !== undefined ? 'Poll \u00B7 voted' : 'Poll';
      barParts.poll.classList.toggle('podium-attn', !!(aud.poll && aud.poll.open && myVotes[aud.poll.id] === undefined));
      barParts.muted.hidden = !aud.muted;
      if (sheet) renderSheet();
      renderBanner();
      renderResults();
    }

    // Bottom sheet for Q&A and the poll.
    function toggleSheet(kind) { if (sheet && sheetKind === kind) { closeSheet(); return; } closeSheet(); sheetKind = kind; sheet = el('div', { id: 'podium-sheet', role: 'dialog', 'aria-label': kind === 'ask' ? 'Questions' : 'Poll' }); ui.appendChild(sheet); ui.classList.add('podium-has-sheet'); renderSheet(); document.addEventListener('keydown', escSheet); }
    function escSheet(e) { if (e.key === 'Escape') closeSheet(); }
    function closeSheet() { if (sheet) sheet.remove(); sheet = null; sheetKind = null; ui.classList.remove('podium-has-sheet'); document.removeEventListener('keydown', escSheet); }
    function renderSheet() {
      if (!sheet) return;
      var draft = sheet.querySelector('textarea') ? sheet.querySelector('textarea').value : '';
      var nickDraft = sheet.querySelector('input') ? sheet.querySelector('input').value : (function () { try { return localStorage.getItem('podium-nick') || ''; } catch (e) { return ''; } })();
      sheet.textContent = '';
      var head = el('div', { class: 'podium-sheet-head' }, [el('strong', { text: sheetKind === 'ask' ? 'Questions' : 'Poll' })]);
      var close = el('button', { type: 'button', class: 'podium-abtn', text: 'Close' }); close.addEventListener('click', closeSheet); head.appendChild(close);
      sheet.appendChild(head);
      if (sheetKind === 'ask') {
        if (canAsk()) {
          var form = el('form', { class: 'podium-ask' });
          var ta = el('textarea', { maxlength: '280', rows: '2', placeholder: 'Ask the speaker\u2026', 'aria-label': 'Your question' }); ta.value = draft;
          var row = el('div', { class: 'podium-row' });
          var nick = null;
          if (aud.settings.nicknames) { nick = el('input', { type: 'text', maxlength: '24', placeholder: 'Your name (optional)', 'aria-label': 'Your name' }); nick.value = nickDraft; row.appendChild(nick); }
          var send = el('button', { type: 'submit', class: 'podium-abtn podium-primary', text: 'Send' });
          row.appendChild(send);
          form.append(ta, row);
          form.addEventListener('submit', function (e) {
            e.preventDefault();
            var text = ta.value.trim();
            if (text.length < 2) return;
            var msg = { t: 'question', text: text.slice(0, 280) };
            if (nick && nick.value.trim()) { msg.nick = nick.value.trim().slice(0, 24); try { localStorage.setItem('podium-nick', msg.nick); } catch (x) { /* private mode */ } }
            sendMsg(msg);
            ta.value = '';
            send.disabled = true; send.textContent = 'Sent'; setTimeout(function () { send.disabled = false; send.textContent = 'Send'; }, 20000);
            toast('Question sent to the speaker');
          });
          sheet.appendChild(form);
        } else sheet.appendChild(el('p', { class: 'podium-note', text: aud.muted ? 'The presenter has muted the room.' : 'Questions are closed.' }));
        var list = el('ul', { class: 'podium-qlist' });
        if (!aud.questions.length) list.appendChild(el('li', { class: 'podium-note', text: 'No questions yet. Yours could be the first.' }));
        aud.questions.forEach(function (q) {
          var li = el('li', { class: 'podium-q' + (q.pinned ? ' podium-pinned' : '') + (q.answered ? ' podium-answered' : '') });
          var up = el('button', { type: 'button', class: 'podium-up' + (myUpvotes[q.id] ? ' podium-mine' : ''), text: '\u25B2 ' + q.upvotes, 'aria-label': 'Upvote' });
          up.addEventListener('click', function () { if (!canAsk()) return; sendMsg({ t: 'upvote', id: q.id }); myUpvotes[q.id] = !myUpvotes[q.id]; try { localStorage.setItem('podium-upvotes-' + cfg.slug, JSON.stringify(myUpvotes)); } catch (x) { /* private mode */ } up.classList.toggle('podium-mine', !!myUpvotes[q.id]); });
          var body = el('div', { class: 'podium-qbody' }, [el('div', { class: 'podium-qtext', text: q.text }), el('div', { class: 'podium-qmeta', text: (q.nick ? q.nick + ' \u00B7 ' : '') + 'slide ' + q.slide + (q.pinned ? ' \u00B7 on screen' : '') + (q.answered ? ' \u00B7 answered' : '') })]);
          li.append(up, body);
          list.appendChild(li);
        });
        sheet.appendChild(list);
      } else {
        var p = aud.poll;
        if (!p) { sheet.appendChild(el('p', { class: 'podium-note', text: 'No poll right now.' })); return; }
        sheet.appendChild(el('h3', { text: p.question }));
        var reveal = p.options.some(function (o) { return typeof o.votes === 'number'; });
        var total = reveal ? p.options.reduce(function (s, o) { return s + (o.votes || 0); }, 0) : 0;
        var opts = el('div', { class: 'podium-options' });
        p.options.forEach(function (o, i) {
          var mine = myVotes[p.id] === i;
          var b = el('button', { type: 'button', class: 'podium-option' + (mine ? ' podium-mine' : ''), disabled: !p.open || aud.muted ? 'true' : null });
          var label = el('span', { class: 'podium-olabel', text: o.text });
          b.appendChild(label);
          if (reveal) { var pct = total ? Math.round((o.votes || 0) * 100 / total) : 0; b.appendChild(el('span', { class: 'podium-obar', style: 'width:' + pct + '%' })); b.appendChild(el('span', { class: 'podium-opct', text: pct + '%' })); }
          if (p.open && !aud.muted) b.addEventListener('click', function () { sendMsg({ t: 'vote', poll: p.id, option: i }); myVotes[p.id] = i; try { localStorage.setItem('podium-votes-' + cfg.slug, JSON.stringify(myVotes)); } catch (x) { /* private mode */ } renderSheet(); renderAudience(); });
          opts.appendChild(b);
        });
        sheet.appendChild(opts);
        sheet.appendChild(el('p', { class: 'podium-note', text: !p.open ? 'Voting is closed.' + (reveal ? ' ' + total + ' votes.' : '') : myVotes[p.id] !== undefined ? 'Thanks, your vote is in. You can still change it while voting is open.' : 'Tap an answer to vote.' }));
      }
    }

    // Floating reactions over the slide (projector and viewers), never in the presenter view.
    function floatEmoji(kind, n, own) {
      if (isPresenterView()) return;
      if (!own && !aud.settings.floatReactions) return;
      var count = Math.min(n, 6);
      for (var i = 0; i < count; i++) {
        if (floatCount > 40) return;
        floatCount++;
        var e = el('span', { class: 'podium-float', 'aria-hidden': 'true', text: EMOJI[kind] || '\u2728' });
        e.style.left = (62 + Math.random() * 30) + '%';
        e.style.animationDelay = (Math.random() * 400) + 'ms';
        e.style.fontSize = (22 + Math.random() * 14) + 'px';
        ui.appendChild(e);
        setTimeout(function () { e.remove(); floatCount--; }, 3200);
      }
    }
    // Pinned question banner (deck windows and viewers).
    function renderBanner() {
      var pinned = aud.live ? aud.questions.filter(function (q) { return q.pinned; })[0] : null;
      if (!pinned || isPresenterView()) { if (banner) { banner.remove(); banner = null; } return; }
      if (!banner) { banner = el('div', { id: 'podium-banner', role: 'status' }); ui.appendChild(banner); }
      banner.textContent = '';
      banner.append(el('span', { class: 'podium-qmark', text: 'Q' }), el('span', { class: 'podium-btext', text: pinned.text }));
      if (pinned.nick) banner.appendChild(el('span', { class: 'podium-bnick', text: '\u2014 ' + pinned.nick }));
    }
    // Poll results on the projector when the presenter shows them.
    function renderResults() {
      var p = aud.live && aud.poll && aud.poll.shown ? aud.poll : null;
      if (!p || !isProjector()) { if (resultsEl) { resultsEl.remove(); resultsEl = null; } return; }
      if (!resultsEl) { resultsEl = el('div', { id: 'podium-results', role: 'status' }); ui.appendChild(resultsEl); }
      resultsEl.textContent = '';
      var total = p.options.reduce(function (s, o) { return s + (o.votes || 0); }, 0);
      resultsEl.appendChild(el('h2', { text: p.question }));
      p.options.forEach(function (o) {
        var pct = total ? Math.round((o.votes || 0) * 100 / total) : 0;
        resultsEl.appendChild(el('div', { class: 'podium-rrow' }, [el('span', { class: 'podium-rlabel', text: o.text }), el('span', { class: 'podium-rtrack' }, [el('span', { class: 'podium-rbar', style: 'width:' + pct + '%' })]), el('span', { class: 'podium-rpct', text: pct + '%' })]));
      });
      resultsEl.appendChild(el('p', { class: 'podium-rtotal', text: total + (total === 1 ? ' vote' : ' votes') + (p.open ? ' so far' : '') }));
    }
    // Presenter HUD counters + moderation popover.
    function renderHudAudience() {
      if (!hud || !hudParts) return;
      if (!hudParts.audience) {
        hudParts.audience = el('button', { type: 'button', class: 'podium-btn', hidden: true, title: 'Reactions and questions from the room' });
        hudParts.audience.addEventListener('click', function () { togglePop('audience'); });
        hud.insertBefore(hudParts.audience, hudParts.code);
      }
      var total = Object.keys(aud.totals).reduce(function (s, k) { return s + aud.totals[k]; }, 0);
      var open = aud.questions.filter(function (q) { return !q.answered; }).length;
      hudParts.audience.hidden = !(aud.live && (aud.settings.reactions || aud.settings.questions || aud.settings.polls));
      hudParts.audience.textContent = EMOJI.clap + ' ' + total + ' \u00B7 ? ' + open + (aud.poll && aud.poll.open ? ' \u00B7 poll' : '');
      hudParts.audience.classList.toggle('podium-attn', open > 0);
      if (popKind === 'audience') renderAudiencePop();
    }
    function renderAudiencePop() {
      if (!pop) return;
      pop.textContent = '';
      pop.appendChild(el('h3', { text: 'From the room' }));
      var tot = el('div', { class: 'podium-totals' });
      KINDS.forEach(function (k) { if (aud.totals[k]) tot.appendChild(el('span', { text: EMOJI[k] + ' ' + aud.totals[k] })); });
      if (!tot.childElementCount) tot.appendChild(el('span', { class: 'podium-note', text: 'No reactions yet' }));
      pop.appendChild(tot);
      var list = el('ul', { class: 'podium-qlist podium-mod' });
      var open = aud.questions.filter(function (q) { return !q.answered; });
      if (!open.length) list.appendChild(el('li', { class: 'podium-note', text: 'No open questions' }));
      open.slice(0, 6).forEach(function (q) {
        var li = el('li', { class: 'podium-q' + (q.pinned ? ' podium-pinned' : '') });
        var body = el('div', { class: 'podium-qbody' }, [el('div', { class: 'podium-qtext', text: q.text }), el('div', { class: 'podium-qmeta', text: '\u25B2 ' + q.upvotes + (q.nick ? ' \u00B7 ' + q.nick : '') + ' \u00B7 slide ' + q.slide })]);
        var acts = el('div', { class: 'podium-qacts' });
        var pin = el('button', { type: 'button', class: 'podium-abtn', text: q.pinned ? 'Unpin' : 'Show' }); pin.addEventListener('click', function () { sendMsg({ t: 'question', op: q.pinned ? 'unpin' : 'pin', id: q.id }); });
        var done = el('button', { type: 'button', class: 'podium-abtn', text: 'Answered' }); done.addEventListener('click', function () { sendMsg({ t: 'question', op: 'answer', id: q.id }); });
        var drop = el('button', { type: 'button', class: 'podium-abtn', text: 'Dismiss' }); drop.addEventListener('click', function () { sendMsg({ t: 'question', op: 'dismiss', id: q.id }); });
        acts.append(pin, done, drop);
        body.appendChild(acts);
        li.appendChild(body);
        list.appendChild(li);
      });
      pop.appendChild(list);
      pop.appendChild(el('p', { class: 'podium-note', text: 'Polls, muting and the full list are on the phone remote.' }));
      var row = el('div', { class: 'podium-row' });
      var close = el('button', { type: 'button', class: 'podium-btn', text: 'Close' }); close.addEventListener('click', closePop);
      row.appendChild(close);
      pop.appendChild(row);
    }
    // Viewers talk through the bridge's raw send (bridge.js; Slidev addon >= 3.1); older addon builds cannot react until rebuilt.
    function sendMsg(message) { if (typeof bridge.sendRaw === 'function') bridge.sendRaw(message); else if (bridge.canSend) bridge.send(message); }
    var ownPending = {};
    var bootAt = Date.now();

    bridge.on('audience', function (m) {
      aud.live = !!m.live; aud.muted = !!m.muted; aud.settings = m.settings || aud.settings;
      if (!aud.live) { aud.questions = []; aud.poll = null; aud.totals = {}; }
      renderAudience(); renderHudAudience();
    });
    bridge.on('reactions', function (m) {
      aud.totals = m.totals || aud.totals;
      if (m.counts) Object.keys(m.counts).forEach(function (k) { var n = m.counts[k] - (ownPending[k] || 0); ownPending[k] = 0; if (n > 0) floatEmoji(k, n, false); });
      renderHudAudience();
    });
    bridge.on('questions', function (m) {
      var before = aud.questions.length;
      aud.questions = Array.isArray(m.items) ? m.items : [];
      if (isPresenterView() && aud.questions.length > before && Date.now() - bootAt > 3000) toast('New question from the room', 3000);
      renderAudience(); renderHudAudience();
    });
    bridge.on('poll', function (m) {
      var hadOpen = aud.poll && aud.poll.open;
      aud.poll = m.poll || null;
      if (isViewer() && aud.poll && aud.poll.open && !hadOpen && myVotes[aud.poll.id] === undefined) { toggleSheet('poll'); if (navigator.vibrate) try { navigator.vibrate(20); } catch (e) { /* unsupported */ } }
      renderAudience(); renderHudAudience();
    });

    // ---- Wire the bridge ---------------------------------------------------------------------------------------
    bridge.on('hello', function (m) {
      presence = { presenters: m.presenters || 0, viewers: m.viewers || 0, windows: m.windows || 0, remotes: m.remotes || 0 };
      if (!bridge.canSend && cid) sendMsg({ t: 'hi', cid: cid });
      render();
    });
    bridge.on('presence', function (m) {
      presence = { presenters: m.presenters || 0, viewers: m.viewers || 0, windows: m.windows || 0, remotes: m.remotes || 0 };
      render();
    });
    bridge.on('info', function (m) { if (typeof m.page === 'number') { presenter.page = m.page; presenter.clicks = Number(m.clicks) || 0; render(); } });
    bridge.on('state', function (m) {
      if (m.state && typeof m.state.page === 'number' && m.channel !== 'podium - pointer') { presenter.page = m.state.page; presenter.clicks = Number(m.state.clicks) || 0; render(); }
    });
    bridge.on('screen', function (m) { screen = { mode: m.mode || 'none', text: m.text || '' }; renderScreen(); });
    bridge.on('pointer', function (m) { renderLaser(m.x, m.y); });
    bridge.on('session', function (m) {
      var was = session && session.live;
      session = m;
      if (m.serverTime) clockOffset = new Date(m.serverTime).getTime() - Date.now();
      // Replayed on join (the session was already running): no transition, nothing to announce.
      if (m.live && !was && !m.replay && bridge.canSend) toast('Live session started' + (m.joinCode ? ' · join code ' + m.joinCode : ''));
      if (!m.live && was) {
        if (bridge.canSend) {
          var r = m.recap;
          toast('Session ended' + (r ? ' · ' + fmtMinutes(r.durationSeconds) + ' · peak ' + r.peakViewers + ' watching' : ''), 7000);
          if (popKind === 'join') closePop();
        } else toast('The live session has ended.', 6000);
      }
      render();
    });
    bridge.on('position', function () { render(); });
    bridge.on('role', function () { if (pill) pill.hidden = true; if (hud) hud.hidden = true; renderScreen(); render(); });
    bridge.on('open', function () { render(); });
    bridge.on('close', function (m) {
      if (m.code === 4410) { showEnded(); return; }
      render();
    });
    document.addEventListener('fullscreenchange', render);
    render();
  }
})();
