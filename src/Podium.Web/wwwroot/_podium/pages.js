// Podium pages viewer: shows a PowerPoint/PDF deck rendered to page images (builder: pdftoppm) like a slide deck.
// Keyboard, touch and click navigation, deep links (/d/<slug>/<page>), preloading, fullscreen, and the sync bridge so
// the phone remote, follow-along, blackout and live sessions work exactly as they do for Slidev decks.
// Expects <main id="podium-pages" data-pages='{"count":n,"aspect":1.7778}'> in the build's index.html.
(function () {
  'use strict';
  var root = document.getElementById('podium-pages');
  if (!root || !window.Podium || !window.Podium.createBridge) return;
  var data;
  try { data = JSON.parse(root.getAttribute('data-pages') || '{}'); } catch (e) { data = {}; }
  var count = Math.max(1, Number(data.count) || 1);
  var slug = (document.querySelector('meta[name="podium-slug"]') || {}).content || (location.pathname.match(/^\/d\/([a-z0-9][a-z0-9-]*)\//) || [])[1];
  if (!slug) return;
  var base = '/d/' + slug + '/';
  var pad = function (n) { return String(n).padStart(3, '0'); };
  var src = function (n) { return base + 'pages/' + pad(n) + '.jpg'; };

  var style = document.createElement('style');
  style.textContent =
    '.podium-pages{display:flex;align-items:center;justify-content:center;background:#000;user-select:none;-webkit-user-select:none;touch-action:pan-y}' +
    '.podium-pages img.pp-slide{max-width:100%;max-height:100%;width:auto;height:auto;display:block;object-fit:contain;opacity:1;transition:opacity .12s ease}' +
    '.podium-pages img.pp-slide.pp-loading{opacity:.6}' +
    '.pp-counter{position:fixed;right:14px;bottom:12px;padding:5px 10px;border-radius:999px;background:rgba(17,17,19,.72);color:#e6e9f0;font:500 12px/1 system-ui,sans-serif;opacity:0;transition:opacity .25s;pointer-events:none}' +
    '.podium-pages.pp-active .pp-counter{opacity:1}' +
    '.pp-zone{position:fixed;top:0;bottom:0;width:50%;cursor:pointer;background:transparent;border:0;padding:0;margin:0}' +
    '.pp-zone-prev{left:0}.pp-zone-next{right:0}.pp-zone:focus-visible{outline:2px solid #7c9cff;outline-offset:-2px}' +
    '.pp-zone-prev{cursor:w-resize}.pp-zone-next{cursor:e-resize}';
  document.head.appendChild(style);

  var img = document.createElement('img');
  img.className = 'pp-slide';
  img.decoding = 'async';
  var counter = document.createElement('div');
  counter.className = 'pp-counter';
  counter.setAttribute('aria-live', 'polite');
  var prevZone = document.createElement('button'); prevZone.type = 'button'; prevZone.className = 'pp-zone pp-zone-prev'; prevZone.setAttribute('aria-label', 'Previous slide');
  var nextZone = document.createElement('button'); nextZone.type = 'button'; nextZone.className = 'pp-zone pp-zone-next'; nextZone.setAttribute('aria-label', 'Next slide');
  root.append(img, counter, prevZone, nextZone);

  var current = 0;
  var bridge = null;
  function fromUrl() {
    var m = location.pathname.match(/\/(\d+)\/?$/);
    var n = m ? Number(m[1]) : 1;
    return Math.min(count, Math.max(1, n || 1));
  }
  var preloaded = {};
  function preload(n) { if (n < 1 || n > count || preloaded[n]) return; preloaded[n] = true; var i = new Image(); i.src = src(n); }
  function show(n, byUser) {
    n = Math.min(count, Math.max(1, Math.round(n)));
    if (n === current) return;
    current = n;
    img.classList.add('pp-loading');
    img.src = src(n);
    img.alt = 'Slide ' + n + ' of ' + count;
    img.onload = function () { img.classList.remove('pp-loading'); };
    counter.textContent = n + ' / ' + count;
    try { history.replaceState(null, '', base + (n === 1 ? '' : n) + location.search + location.hash); } catch (e) { /* older browsers */ }
    preload(n + 1); preload(n + 2); preload(n - 1);
    flash();
    if (bridge) { if (byUser) bridge.userNavigated(); else bridge.changed(); }
  }
  var flashTimer = null;
  function flash() {
    root.classList.add('pp-active');
    clearTimeout(flashTimer);
    flashTimer = setTimeout(function () { root.classList.remove('pp-active'); }, 1800);
  }
  function nav(action, page, byUser) {
    switch (action) {
      case 'next': case 'nextSlide': show(current + 1, byUser); break;
      case 'prev': case 'prevSlide': show(current - 1, byUser); break;
      case 'first': show(1, byUser); break;
      case 'last': show(count, byUser); break;
      case 'go': if (page) show(page, byUser); break;
    }
  }

  document.addEventListener('keydown', function (e) {
    if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey) return;
    var t = e.target; if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
    switch (e.key) {
      case 'ArrowRight': case 'ArrowDown': case 'PageDown': case ' ': case 'Enter': case 'n': case 'j': nav('next', 0, true); e.preventDefault(); break;
      case 'ArrowLeft': case 'ArrowUp': case 'PageUp': case 'Backspace': case 'p': case 'k': nav('prev', 0, true); e.preventDefault(); break;
      case 'Home': nav('first', 0, true); e.preventDefault(); break;
      case 'End': nav('last', 0, true); e.preventDefault(); break;
      case 'f': case 'F': toggleFullscreen(); break;
    }
  });
  prevZone.addEventListener('click', function () { nav('prev', 0, true); });
  nextZone.addEventListener('click', function () { nav('next', 0, true); });
  var touchX = null, touchY = null;
  root.addEventListener('touchstart', function (e) { if (e.touches.length === 1) { touchX = e.touches[0].clientX; touchY = e.touches[0].clientY; } }, { passive: true });
  root.addEventListener('touchend', function (e) {
    if (touchX === null) return;
    var dx = e.changedTouches[0].clientX - touchX, dy = e.changedTouches[0].clientY - touchY;
    touchX = touchY = null;
    if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5) { nav(dx < 0 ? 'next' : 'prev', 0, true); e.preventDefault(); }
  });
  root.addEventListener('mousemove', flash);
  function toggleFullscreen() {
    if (document.fullscreenElement) { if (document.exitFullscreen) document.exitFullscreen(); }
    else if (document.documentElement.requestFullscreen) document.documentElement.requestFullscreen().catch(function () {});
  }
  root.addEventListener('dblclick', toggleFullscreen);
  window.addEventListener('popstate', function () { show(fromUrl(), true); });

  show(fromUrl(), false);
  bridge = window.Podium.createBridge({
    kind: 'pages',
    slug: slug,
    total: function () { return count; },
    page: function () { return current; },
    go: function (n) { show(n, false); },
    nav: function (action, page) { nav(action, page, false); },
  });
})();
