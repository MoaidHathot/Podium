namespace Podium.Web.Storage;

/// <summary>
/// A minimal "deck" used by the dev-seed endpoint: three slides addressed by the path's last segment, driven by the
/// Podium sync protocol exactly like the real addon (hello/state/info/nav/presence/screen). Lets CI exercise the relay,
/// remote and live.js in a browser without building a Slidev deck.
/// </summary>
public static class FixtureDeck
{
    public const string IndexHtml = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Fixture deck</title>
        <style>body{margin:0;font:32px system-ui;background:#111;color:#eee;display:grid;place-items:center;height:100vh}#slide{font-size:120px}</style>
        </head><body>
        <div><div id="slide">1</div><div id="status">starting</div></div>
        <script>
        (function () {
          var total = 3;
          var slug = location.pathname.split('/')[2];
          var m = location.pathname.match(/\/(\d+)\/?$/);
          var page = m ? Math.min(total, Math.max(1, Number(m[1]))) : 1;
          var canSend = false, ws = null;
          function show(p) { page = p; document.getElementById('slide').textContent = String(p); history.replaceState(null, '', '/d/' + slug + '/' + p); if (canSend) report(); }
          function report() { if (ws && ws.readyState === 1) { ws.send(JSON.stringify({ t: 'info', page: page, total: total, clicks: 0, clicksTotal: 0 })); ws.send(JSON.stringify({ t: 'state', channel: 'fixture - shared', state: { page: page, clicks: 0 } })); } }
          function connect() {
            ws = new WebSocket((location.protocol === 'https:' ? 'wss' : 'ws') + '://' + location.host + '/ws/sync/' + slug);
            ws.onopen = function () { document.getElementById('status').textContent = 'connected'; window.__podiumSocketActive && window.__podiumSocketActive(true); };
            ws.onmessage = function (ev) {
              var msg = JSON.parse(ev.data);
              if (msg.t === 'hello') { canSend = !!msg.canSend; document.body.dataset.canSend = String(canSend); if (canSend) report(); }
              if (msg.t === 'nav' && canSend) { if (msg.action === 'next') show(Math.min(total, page + 1)); if (msg.action === 'prev') show(Math.max(1, page - 1)); if (msg.action === 'first') show(1); if (msg.action === 'last') show(total); if (msg.action === 'go' && msg.page) show(Math.min(total, Math.max(1, msg.page))); }
              if (msg.t === 'state' && msg.state && typeof msg.state.page === 'number' && !canSend) show(msg.state.page);
              if (msg.t === 'presence') document.body.dataset.viewers = String(msg.viewers);
              if (msg.t === 'screen' && !canSend) { var s = document.getElementById('screen'); if (msg.mode === 'none') { if (s) s.remove(); } else { if (!s) { s = document.createElement('div'); s.id = 'screen'; s.style.cssText = 'position:fixed;inset:0;background:#000;color:#ddd;display:grid;place-items:center'; document.body.appendChild(s); } s.textContent = msg.text || ''; } }
              if (msg.t === 'build') window.__podiumNewBuild && window.__podiumNewBuild(msg.build);
            };
            ws.onclose = function () { document.getElementById('status').textContent = 'disconnected'; window.__podiumSocketActive && window.__podiumSocketActive(false); setTimeout(connect, 1000); };
          }
          document.addEventListener('keydown', function (e) { if (e.key === 'ArrowRight') show(Math.min(total, page + 1)); if (e.key === 'ArrowLeft') show(Math.max(1, page - 1)); });
          show(page); connect();
        })();
        </script>
        </body></html>
        """;
}
