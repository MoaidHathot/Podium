// Podium adapter for presenterm HTML exports. presenterm's own script shows one `div.container` at a time and
// listens for ArrowLeft/ArrowRight on the document; this adapter drives that script (synthetic key events), tracks
// the shown slide (mutation observer), adds deep links (/d/<slug>/<n>), touch/click navigation and centring of the
// scaled slide, and connects the deck to Podium's sync bridge so the phone remote, follow-along, blackout and live
// sessions work for presenterm decks exactly as for Slidev decks. It never edits the exported slides themselves.
(function () {
  'use strict';
  if (!window.Podium || !window.Podium.createBridge) return;
  var slug = (document.querySelector('meta[name="podium-slug"]') || {}).content || (location.pathname.match(/^\/d\/([a-z0-9][a-z0-9-]*)\//) || [])[1];
  if (!slug) return;
  var base = '/d/' + slug + '/';

  function boot() {
    var slides = Array.prototype.slice.call(document.querySelectorAll('body > div.container'));
    if (!slides.length) return;
    var count = slides.length;
    var bridge = null;
    var pendingKind = null; // 'user' | 'program': who caused the slide change the observer is about to see

    function current() {
      for (var i = 0; i < slides.length; i++) if (!slides[i].classList.contains('hidden')) return i + 1;
      return 1;
    }
    function key(name) { document.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true })); }
    function go(n, byUser) {
      n = Math.min(count, Math.max(1, Math.round(n)));
      var cur = current();
      if (n === cur) return;
      pendingKind = byUser ? 'user' : 'program';
      var steps = Math.abs(n - cur), name = n > cur ? 'ArrowRight' : 'ArrowLeft';
      for (var i = 0; i < steps; i++) key(name);
    }
    function nav(action, page, byUser) {
      switch (action) {
        case 'next': case 'nextSlide': go(current() + 1, byUser); break;
        case 'prev': case 'prevSlide': go(current() - 1, byUser); break;
        case 'first': go(1, byUser); break;
        case 'last': go(count, byUser); break;
        case 'go': if (page) go(page, byUser); break;
      }
    }

    // Keep the URL and the room in step with whatever is shown, however it got there (keys, clicks, the remote).
    // Mutation callbacks run after the change, so the cause is remembered in pendingKind by whoever triggered it;
    // a change nobody announced came from the person at the keyboard.
    var last = current();
    var observer = new MutationObserver(function () {
      var now = current();
      var kind = pendingKind || 'user';
      pendingKind = null;
      if (now === last) return;
      last = now;
      try { history.replaceState(null, '', base + (now === 1 ? '' : now) + location.search + location.hash); } catch (e) { /* older browsers */ }
      if (bridge) { if (kind === 'user') bridge.userNavigated(); else bridge.changed(); }
    });
    slides.forEach(function (s) { observer.observe(s, { attributes: true, attributeFilter: ['class'] }); });
    // A keyboard press by the person at the machine is a user navigation; presenterm's handler runs first (registered
    // earlier), the observer fires after the class change.
    document.addEventListener('keydown', function (e) {
      if (!e.isTrusted || e.defaultPrevented) return;
      var t = e.target; if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      switch (e.key) {
        case 'ArrowLeft': case 'ArrowRight': pendingKind = 'user'; break;
        case ' ': case 'PageDown': case 'ArrowDown': case 'Enter': pendingKind = 'user'; key('ArrowRight'); e.preventDefault(); break;
        case 'PageUp': case 'ArrowUp': case 'Backspace': pendingKind = 'user'; key('ArrowLeft'); e.preventDefault(); break;
        case 'Home': go(1, true); break;
        case 'End': go(count, true); break;
        case 'f': case 'F':
          if (document.fullscreenElement) { if (document.exitFullscreen) document.exitFullscreen(); }
          else if (document.documentElement.requestFullscreen) document.documentElement.requestFullscreen().catch(function () {});
          break;
      }
    }, true);
    // Tap/click: right half forward, left half back (links inside a slide and Podium's own UI keep working).
    document.addEventListener('click', function (e) {
      if (e.defaultPrevented || e.button !== 0) return;
      var el = e.target;
      while (el && el !== document.documentElement) { if (el.tagName === 'A' || el.tagName === 'BUTTON' || (el.id && el.id.indexOf('podium-') === 0) || (el.className && String(el.className).indexOf('podium-') >= 0)) return; el = el.parentNode; }
      pendingKind = 'user';
      key(e.clientX < window.innerWidth / 2 ? 'ArrowLeft' : 'ArrowRight');
    });
    var touchX = null, touchY = null;
    document.addEventListener('touchstart', function (e) { if (e.touches.length === 1) { touchX = e.touches[0].clientX; touchY = e.touches[0].clientY; } }, { passive: true });
    document.addEventListener('touchend', function (e) {
      if (touchX === null) return;
      var dx = e.changedTouches[0].clientX - touchX, dy = e.changedTouches[0].clientY - touchY;
      touchX = touchY = null;
      if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5) { pendingKind = 'user'; key(dx < 0 ? 'ArrowRight' : 'ArrowLeft'); }
    });

    // presenterm scales the body from its top-left corner; centre it so the slide sits in the middle of the screen.
    function centre() {
      var body = document.body;
      var cs = getComputedStyle(body);
      var w = parseFloat(cs.width), h = parseFloat(cs.height);
      if (!w || !h) return;
      var s = Math.min(window.innerWidth / w, window.innerHeight / h);
      var dx = Math.max(0, (window.innerWidth - w * s) / 2), dy = Math.max(0, (window.innerHeight - h * s) / 2);
      body.style.transformOrigin = 'top left';
      body.style.transform = 'translate(' + dx.toFixed(2) + 'px, ' + dy.toFixed(2) + 'px) scale(' + s.toFixed(5) + ')';
    }
    window.addEventListener('resize', function () { setTimeout(centre, 0); });
    setTimeout(centre, 0);

    // Deep link on arrival.
    var m = location.pathname.match(/\/(\d+)\/?$/);
    if (m) go(Number(m[1]), false);

    bridge = window.Podium.createBridge({
      kind: 'presenterm',
      slug: slug,
      total: function () { return count; },
      page: current,
      go: function (n) { go(n, false); },
      nav: function (action, page) { nav(action, page, false); },
    });
  }

  // presenterm's own script sets the slides up on DOMContentLoaded; run after it.
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function () { setTimeout(boot, 0); });
  else setTimeout(boot, 0);
})();
