// Injected into every served deck by Podium. Reacts to new builds of the deck:
//   - a Slidev deck receives {"t":"build"} over the Podium sync socket (see the injected addon), which calls
//     window.__podiumNewBuild(buildId); other deck kinds fall back to polling /api/decks/<slug>/version.
//   - Never yank a presenter: the presenter view only shows a notice. Visible audience tabs show a notice with a
//     Reload button; hidden tabs reload silently. The current slide survives a reload (it is in the URL).
(function () {
  'use strict';
  var script = document.currentScript || document.querySelector('script[src*="/_podium/live.js"]');
  if (!script) return;
  var slug = script.getAttribute('data-slug');
  var build = script.getAttribute('data-build');
  if (!slug || !build) return;

  var isPresenter = /\/presenter(\/|$)/.test(location.pathname) || /\/remote(\/|$)/.test(location.pathname);
  var isPrint = /[?&]print=/.test(location.search);
  var pending = null;
  var noticeEl = null;

  function reload() { location.reload(); }

  function showNotice(message, canReload) {
    if (noticeEl) noticeEl.remove();
    var el = document.createElement('div');
    el.setAttribute('role', 'status');
    el.style.cssText = 'position:fixed;left:50%;bottom:18px;transform:translateX(-50%);z-index:2147483647;display:flex;gap:10px;align-items:center;' +
      'padding:10px 14px;border-radius:10px;background:rgba(19,23,32,.94);color:#e6e9f0;border:1px solid #354055;box-shadow:0 8px 30px rgba(0,0,0,.45);' +
      'font:14px/1.4 ui-sans-serif,system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;backdrop-filter:blur(8px)';
    var text = document.createElement('span'); text.textContent = message; el.appendChild(text);
    if (canReload) {
      var btn = document.createElement('button'); btn.type = 'button'; btn.textContent = 'Reload';
      btn.style.cssText = 'font:inherit;font-weight:600;padding:5px 10px;border-radius:6px;border:1px solid #7c9cff;background:#7c9cff;color:#0b0d12;cursor:pointer';
      btn.addEventListener('click', reload); el.appendChild(btn);
    }
    var close = document.createElement('button'); close.type = 'button'; close.setAttribute('aria-label', 'Dismiss'); close.textContent = '\u00d7';
    close.style.cssText = 'font:16px/1 inherit;padding:2px 6px;border:0;background:transparent;color:#9aa3b5;cursor:pointer';
    close.addEventListener('click', function () { el.remove(); noticeEl = null; });
    el.appendChild(close);
    document.body.appendChild(el);
    noticeEl = el;
  }

  function onNewBuild(newBuild) {
    if (!newBuild || newBuild === build || newBuild === pending) return;
    pending = newBuild;
    if (isPrint) return;
    if (isPresenter) {
      showNotice('A new version of this deck is available. Reload when convenient.', true);
      return;
    }
    if (document.visibilityState === 'hidden') { reload(); return; }
    showNotice('A new version of this deck is available.', true);
  }

  // Hidden audience tabs catch up the moment they are shown again... no: reload while still hidden so the
  // viewer never sees a flash; if a notice is pending when the tab becomes visible, leave it to the user.
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'hidden' && pending && !isPresenter && !isPrint) reload();
  });

  window.__podiumNewBuild = onNewBuild;
  window.__podiumBuild = build;

  // Polling fallback (non-Slidev decks, or until the socket connects). Backs off and stops on auth loss.
  var interval = 30000;
  var failures = 0;
  var socketActive = false;
  window.__podiumSocketActive = function (active) { socketActive = !!active; };

  function check() {
    if (socketActive) return schedule();
    if (document.visibilityState === 'hidden') return schedule();
    fetch('/api/decks/' + encodeURIComponent(slug) + '/version', { credentials: 'same-origin', cache: 'no-store' })
      .then(function (r) {
        if (r.status === 401 || r.status === 404) { failures = 99; return null; }
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      })
      .then(function (v) { if (!v) return; failures = 0; onNewBuild(v.build); schedule(); })
      .catch(function () { failures++; schedule(); });
  }
  function schedule() { if (failures > 10) return; setTimeout(check, Math.min(interval * (1 + failures), 180000)); }
  schedule();

  // Offline cache (per-deck setting, secure contexts only). The worker is scoped to this deck; presenters precache
  // the whole build from the manifest, everyone else caches what they visit. See /_podium/sw.js.
  var offline = script.getAttribute('data-offline') === '1';
  var presenterSession = script.getAttribute('data-presenter') === '1';
  if (offline && !isPrint && 'serviceWorker' in navigator && window.isSecureContext) {
    var scope = '/d/' + slug + '/';
    navigator.serviceWorker.register(scope + '_podium/sw.js', { scope: scope }).then(function (reg) {
      var post = function (msg) { var w = reg.active || reg.waiting || reg.installing; if (w) w.postMessage(msg); };
      var ready = navigator.serviceWorker.ready;
      ready.then(function () {
        post({ type: 'activate-build', build: build });
        if (!presenterSession) return;
        fetch(scope + '_podium/manifest.json', { credentials: 'same-origin' })
          .then(function (r) { return r.ok ? r.json() : null; })
          .then(function (m) {
            if (!m || !m.variants) return;
            var files = m.variants.site || m.variants['site-public'] || [];
            post({ type: 'precache', build: m.build || build, files: files });
          })
          .catch(function () {});
      });
    }).catch(function () {});
  } else if (!isPrint && 'serviceWorker' in navigator && window.isSecureContext) {
    // Setting turned off (or no longer allowed): drop this deck's worker and its caches.
    navigator.serviceWorker.getRegistration('/d/' + slug + '/').then(function (reg) {
      if (!reg || new URL(reg.scope).pathname !== '/d/' + slug + '/') return;
      reg.unregister();
      if (window.caches) caches.keys().then(function (keys) { keys.filter(function (k) { return k.indexOf('podium-' + slug + '-') === 0; }).forEach(function (k) { caches.delete(k); }); });
    }).catch(function () {});
  }
})();
