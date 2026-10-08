// Shared client helpers: API calls with CSRF header, relative times, toasts, copy-to-clipboard.
(function () {
  'use strict';

  const HEADER = 'X-Podium-Request';

  async function api(method, url, body) {
    const res = await fetch(url, {
      method,
      headers: { [HEADER]: '1', ...(body !== undefined ? { 'content-type': 'application/json' } : {}) },
      body: body !== undefined ? JSON.stringify(body) : undefined,
      credentials: 'same-origin',
    });
    if (res.status === 401) { location.href = '/login?returnUrl=' + encodeURIComponent(location.pathname + location.search); throw new Error('Signed out'); }
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    if (!res.ok) {
      const msg = (data && data.error) || (data && data.title) || (typeof data === 'string' && data) || `HTTP ${res.status}`;
      throw new Error(msg);
    }
    return data;
  }

  function toast(msg, kind) {
    const el = document.getElementById('toast');
    if (!el) return;
    el.textContent = msg;
    el.style.borderColor = kind === 'error' ? 'var(--bad)' : kind === 'ok' ? 'var(--ok)' : '';
    el.classList.add('show');
    clearTimeout(el._t);
    el._t = setTimeout(() => el.classList.remove('show'), 2600);
  }

  const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  function relative(iso) {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    const diff = (d.getTime() - Date.now()) / 1000;
    const abs = Math.abs(diff);
    if (abs < 45) return 'just now';
    if (abs < 3600) return rtf.format(Math.round(diff / 60), 'minute');
    if (abs < 86400) return rtf.format(Math.round(diff / 3600), 'hour');
    if (abs < 86400 * 30) return rtf.format(Math.round(diff / 86400), 'day');
    if (abs < 86400 * 365) return rtf.format(Math.round(diff / (86400 * 30)), 'month');
    return rtf.format(Math.round(diff / (86400 * 365)), 'year');
  }

  function refreshTimes(root) {
    (root || document).querySelectorAll('time[datetime]').forEach((t) => {
      t.textContent = relative(t.getAttribute('datetime'));
      t.title = new Date(t.getAttribute('datetime')).toLocaleString();
    });
  }

  async function copy(text) {
    try { await navigator.clipboard.writeText(text); toast('Copied to clipboard', 'ok'); }
    catch { toast('Copy failed', 'error'); }
  }

  document.addEventListener('click', (e) => {
    const btn = e.target.closest('[data-copy]');
    if (btn) { e.preventDefault(); copy(btn.getAttribute('data-copy')); }
    const share = e.target.closest('[data-share-url]');
    if (share) {
      e.preventDefault();
      const data = { url: share.getAttribute('data-share-url'), title: share.getAttribute('data-share-title') || document.title, text: share.getAttribute('data-share-text') || undefined };
      // Native share sheet where available (phones, Edge/Chrome on Windows); otherwise copy.
      if (navigator.share) navigator.share(data).catch(() => {}); else copy(data.url);
    }
  });

  document.addEventListener('DOMContentLoaded', () => {
    refreshTimes();
    setInterval(refreshTimes, 60_000);
    // Narrow screens get the short placeholder an input declares (the long one explains what the search covers).
    if (matchMedia('(max-width: 640px)').matches) for (const el of document.querySelectorAll('[data-placeholder-short]')) el.placeholder = el.dataset.placeholderShort;
  });

  window.Podium = { api, toast, relative, refreshTimes, copy };

  // Installable app: app-shell worker + the browser's install prompt when it offers one (Chrome/Edge; iOS uses
  // Share > Add to Home Screen, mentioned in the shortcuts help).
  if ('serviceWorker' in navigator && window.isSecureContext) navigator.serviceWorker.register('/sw.js').catch(() => {});
  let installPrompt = null;
  const installBtn = document.getElementById('install-app');
  window.addEventListener('beforeinstallprompt', (e) => { e.preventDefault(); installPrompt = e; installBtn?.classList.remove('hidden'); });
  installBtn?.addEventListener('click', async () => { if (!installPrompt) return; installPrompt.prompt(); try { await installPrompt.userChoice; } catch {} installPrompt = null; installBtn.classList.add('hidden'); });
  window.addEventListener('appinstalled', () => { installBtn?.classList.add('hidden'); toast('Podium installed', 'ok'); });
})();
