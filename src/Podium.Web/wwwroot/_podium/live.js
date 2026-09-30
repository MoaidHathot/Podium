// Injected into every served deck by Podium. Detects new builds and reloads so a `git push` updates open decks.
// The current slide survives the reload because Slidev keeps it in the URL.
(function () {
  'use strict';
  var script = document.currentScript || document.querySelector('script[src*="/_podium/live.js"]');
  if (!script) return;
  var slug = script.getAttribute('data-slug');
  var build = script.getAttribute('data-build');
  if (!slug || !build) return;

  var interval = 20000;
  var failures = 0;

  function check() {
    if (document.visibilityState === 'hidden') return schedule();
    fetch('/api/decks/' + encodeURIComponent(slug) + '/version', { credentials: 'same-origin', cache: 'no-store' })
      .then(function (r) {
        if (r.status === 401 || r.status === 404) { failures = 99; return null; } // sandboxed or no longer visible: stop
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      })
      .then(function (v) {
        if (!v) return;
        failures = 0;
        if (v.build && v.build !== build) {
          // Small delay so the presenter is not yanked mid-transition.
          setTimeout(function () { location.reload(); }, 800);
          return;
        }
        schedule();
      })
      .catch(function () { failures++; schedule(); });
  }

  function schedule() {
    if (failures > 10) return;
    setTimeout(check, Math.min(interval * (1 + failures), 120000));
  }

  document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'visible') check(); });
  schedule();
})();
