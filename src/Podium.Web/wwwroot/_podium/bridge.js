// Podium sync bridge for deck kinds that are not Slidev (presenterm exports, the pages viewer for PowerPoint/PDF).
// Gives such a deck exactly what the Slidev addon gives: a socket to the deck's room, a role handshake, execution
// of remote commands on the primary window, position reports so viewers (and the phone) follow, the laser pointer,
// a shared timer. The adapter that owns the slides supplies navigation; this file never touches the slide DOM.
//
//   var bridge = Podium.createBridge({
//     kind: 'pages',                         // reported to live-ui.js
//     slug: 'my-deck',
//     total: function () { return 24; },     // slide count
//     page: function () { return 3; },       // current 1-based slide
//     go: function (page) { ... },           // show a slide (programmatic: following the presenter / "Jump to live")
//     nav: function (action, page) { ... },  // next | prev | first | last | go (from the remote)
//   });
//   bridge.changed();                        // the adapter calls this whenever the shown slide changes
//   bridge.userNavigated();                  // ... and this when the user did it (viewers stop following)
(function () {
  'use strict';
  window.Podium = window.Podium || {};
  if (window.Podium.createBridge) return;

  window.Podium.createBridge = function (opts) {
    var slug = opts.slug;
    var url = (location.protocol === 'https:' ? 'wss' : 'ws') + '://' + location.host + '/ws/sync/' + encodeURIComponent(slug);
    var socket = null, backoff = 1000, closedByPage = false, canSend = false, connected = false, following = true;
    var pending = [], listeners = {}, lastInfo = '';
    var timer = { status: 'stopped', slides: {}, startedAt: 0, pausedAt: 0 };
    var presenterPage = null;
    var NAV = 'podium - nav', SHARED = 'podium - shared', POINTER = 'podium - pointer';

    function emit(type, data) {
      var set = listeners[type];
      if (!set) return;
      set.slice().forEach(function (fn) { try { fn(data); } catch (e) { /* a listener must never break the relay */ } });
    }
    function send(payload) {
      if (socket && socket.readyState === WebSocket.OPEN) socket.send(payload);
      else { pending.push(payload); if (pending.length > 20) pending.shift(); }
    }
    function position() { return { page: opts.page(), total: opts.total(), clicks: 0, clicksTotal: 0 }; }

    // Presenting windows publish where they are: `info` for the remote, `state` so viewers (and late joiners, through
    // the server's replay) follow. Same channel semantics as Slidev's shared state.
    function report(force) {
      emit('position', position());
      if (!canSend) return;
      var pos = position();
      var info = JSON.stringify({ t: 'info', page: pos.page, total: pos.total, clicks: 0, clicksTotal: 0, role: 'play' });
      if (!force && info === lastInfo) return;
      lastInfo = info;
      send(info);
      send(JSON.stringify({ t: 'state', channel: NAV, state: { page: pos.page, clicks: 0, clicksTotal: 0, lastUpdate: { type: 'presenter', time: Date.now() } } }));
    }
    function publishTimer() { send(JSON.stringify({ t: 'state', channel: SHARED, state: { timer: timer } })); emit('timer', { timer: timer }); }
    function applyTimer(op) {
      var now = Date.now(), running = timer.status === 'running';
      if (op === 'reset') timer = { status: 'stopped', slides: {}, startedAt: 0, pausedAt: 0 };
      else if (op === 'pause') { if (!running) return; timer = { status: 'paused', slides: {}, startedAt: timer.startedAt, pausedAt: now }; }
      else if (op === 'start' || op === 'toggle') {
        if (running) { if (op === 'start') return; timer = { status: 'paused', slides: {}, startedAt: timer.startedAt, pausedAt: now }; }
        else if (timer.status === 'paused') timer = { status: 'running', slides: {}, startedAt: now - (timer.pausedAt - timer.startedAt), pausedAt: 0 };
        else timer = { status: 'running', slides: {}, startedAt: now, pausedAt: 0 };
      } else return;
      publishTimer();
    }

    function connect() {
      if (closedByPage) return;
      try { socket = new WebSocket(url); } catch (e) { retry(); return; }
      socket.addEventListener('open', function () {
        backoff = 1000; connected = true;
        while (pending.length && socket && socket.readyState === WebSocket.OPEN) socket.send(pending.shift());
        if (window.__podiumSocketActive) window.__podiumSocketActive(true);
        emit('open', {});
      });
      socket.addEventListener('message', function (ev) {
        var msg;
        try { msg = JSON.parse(ev.data); } catch (e) { return; }
        switch (msg.t) {
          case 'hello':
            canSend = !!msg.canSend;
            if (canSend) { send(JSON.stringify({ t: 'hi', role: 'play' })); report(true); if (timer.status !== 'stopped') publishTimer(); }
            break;
          case 'build':
            if (msg.build && window.__podiumNewBuild) window.__podiumNewBuild(msg.build);
            break;
          case 'nav':
            if (canSend && typeof msg.action === 'string') opts.nav(msg.action, typeof msg.page === 'number' ? msg.page : undefined);
            break;
          case 'pointer':
            // The primary window re-broadcasts the dot as state so every viewer's screen shows it (and the server
            // replays the last position to newcomers; null clears it).
            if (canSend) {
              var dot = typeof msg.x === 'number' && typeof msg.y === 'number' ? { x: msg.x, y: msg.y } : null;
              send(JSON.stringify({ t: 'state', channel: POINTER, state: { x: dot ? dot.x : null, y: dot ? dot.y : null } }));
              emit('pointer', dot || { x: null, y: null });
            }
            break;
          case 'timer':
            if (canSend) applyTimer(msg.op);
            break;
          case 'info':
            if (typeof msg.page === 'number') presenterPage = msg.page;
            break;
          case 'state':
            if (!msg.state || typeof msg.state !== 'object') break;
            if (msg.channel === NAV && typeof msg.state.page === 'number') {
              presenterPage = msg.state.page;
              if (!canSend && following && msg.state.page !== opts.page()) opts.go(msg.state.page);
            } else if (msg.channel === SHARED && msg.state.timer && typeof msg.state.timer === 'object') {
              if (!canSend) { timer = msg.state.timer; emit('timer', { timer: timer }); }
            } else if (msg.channel === POINTER) {
              emit('pointer', { x: msg.state.x, y: msg.state.y });
            }
            break;
        }
        emit(String(msg.t || ''), msg);
        emit('*', msg);
      });
      socket.addEventListener('close', function (ev) {
        socket = null; connected = false;
        if (window.__podiumSocketActive) window.__podiumSocketActive(false);
        emit('close', { code: ev.code });
        if (ev.code === 4403 || ev.code === 4404 || ev.code === 4410) return;
        retry();
      });
      socket.addEventListener('error', function () { /* close follows */ });
    }
    function retry() { if (closedByPage) return; setTimeout(connect, backoff); backoff = Math.min(backoff * 2, 30000); }
    window.addEventListener('pagehide', function () { closedByPage = true; if (socket) socket.close(); });

    var bridge = {
      protocol: 3,
      kind: opts.kind,
      slug: slug,
      get role() { return 'play'; },
      get canSend() { return canSend; },
      get connected() { return connected; },
      get following() { return following; },
      get presenterPage() { return presenterPage; },
      get timer() { return timer; },
      setFollowing: function (on) { following = !!on; if (following && !canSend && presenterPage && presenterPage !== opts.page()) opts.go(presenterPage); },
      position: position,
      send: function (message) { if (canSend) send(JSON.stringify(message)); },
      on: function (type, fn) { (listeners[type] = listeners[type] || []).push(fn); return function () { var i = (listeners[type] || []).indexOf(fn); if (i >= 0) listeners[type].splice(i, 1); }; },
      go: function (page) { opts.go(page); },
      nav: function (action, page) { opts.nav(action, page); },
      changed: function () { report(false); },
      userNavigated: function () { if (!canSend) following = false; report(false); },
    };
    connect();
    window.__podium = bridge;
    window.dispatchEvent(new CustomEvent('podium:bridge'));
    return bridge;
  };
})();
