// Podium offline cache. Registered per deck with scope /d/<slug>/ so a flaky venue network cannot take the slides
// down mid-talk. Served by Podium itself (never deck code).
//
//   - Cache per build: "podium-<slug>-<build>". A page tells us its build on load; older caches of the same deck go.
//   - Presenters precache every file of their site variant (list from /_podium/manifest.json, sent by the page);
//     everyone else caches what they visit.
//   - Navigations: network first, cached index as the offline fallback (the slide number lives in the URL).
//   - Hashed assets (/assets/): cache first. Other files: network first with cache fallback.
//   - Never touched: Podium tooling under _podium/, speaker notes, slide sheets, session state, QR codes, the
//     version probe, anything carrying a share/view token, non-GET and cross-origin requests.
//   - The phone remote page and its stylesheet/script are kept network-first in a small side cache, so a reload
//     during a wifi blip still shows the remote while its socket reconnects (the page carries no notes itself).
'use strict';

const scopePath = new URL(self.registration.scope).pathname;            // "/d/<slug>/"
const slug = scopePath.split('/')[2];
const NEVER = /\/(_podium\/|notes\.json$|slides\.(jpg|json)$|session\.json$|qr\.svg$)/;
const REMOTE_PAGE = new RegExp(`^${scopePath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}remote$`);
const REMOTE_ASSETS = /^\/(css\/remote\.css|js\/remote\.js)$/;
const remoteCacheName = `podium-${slug}-remote`;
let currentBuild = null;

const cacheName = (build) => `podium-${slug}-${build}`;

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));

self.addEventListener('message', (e) => {
  const msg = e.data || {};
  if (msg.type === 'activate-build' && typeof msg.build === 'string') {
    currentBuild = msg.build;
    e.waitUntil(dropOtherBuilds(msg.build));
  } else if (msg.type === 'precache' && typeof msg.build === 'string' && Array.isArray(msg.files)) {
    currentBuild = msg.build;
    e.waitUntil(precache(msg.build, msg.files.slice(0, 5000)));
  }
});

async function dropOtherBuilds(build) {
  const keys = await caches.keys();
  await Promise.all(keys.filter((k) => k.startsWith(`podium-${slug}-`) && k !== cacheName(build) && k !== remoteCacheName).map((k) => caches.delete(k)));
}

async function precache(build, files) {
  const cache = await caches.open(cacheName(build));
  const existing = new Set((await cache.keys()).map((r) => new URL(r.url).pathname));
  const wanted = files.map((f) => scopePath + String(f).replace(/^\/+/, '')).filter((p) => !NEVER.test(p) && !existing.has(p));
  // Small batches keep the venue connection usable for the talk itself.
  for (let i = 0; i < wanted.length; i += 8) {
    await Promise.all(wanted.slice(i, i + 8).map(async (p) => {
      try {
        const res = await fetch(p, { credentials: 'same-origin' });
        if (res.ok) await cache.put(p, stripVary(res));
      } catch { /* offline already; the page keeps working from what we have */ }
    }));
  }
  // The index (any slide number resolves to it) is the offline entry point.
  try {
    const res = await fetch(scopePath, { credentials: 'same-origin', headers: { accept: 'text/html' } });
    if (res.ok) await cache.put(scopePath, stripVary(res));
  } catch { /* ignore */ }
}

function stripVary(res) {
  // Cache API matching honours Vary; our deck responses vary on Cookie, which is invisible to the worker. The
  // cache is per browser profile anyway, so dropping the header is safe here.
  const headers = new Headers(res.headers);
  headers.delete('vary');
  return new Response(res.body, { status: res.status, statusText: res.statusText, headers });
}

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;
  // The remote (a page inside this scope) and the two files it loads from the app shell.
  if (REMOTE_PAGE.test(url.pathname) && req.mode === 'navigate') { e.respondWith(sideCache(req)); return; }
  if (REMOTE_ASSETS.test(url.pathname)) { e.respondWith(sideCache(req)); return; }
  if (!url.pathname.startsWith(scopePath)) return;
  if (NEVER.test(url.pathname) || /(^|&)(share|podium_vt)=/.test(url.search.slice(1))) return;
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/ws/')) return;

  if (req.mode === 'navigate') { e.respondWith(navigation(req)); return; }
  if (url.pathname.includes('/assets/')) { e.respondWith(cacheFirst(req)); return; }
  e.respondWith(networkFirst(req));
});

async function sideCache(req) {
  const cache = await caches.open(remoteCacheName);
  try {
    const res = await fetch(req);
    if (res.ok) cache.put(req.url, stripVary(res.clone()));
    return res;
  } catch {
    return (await cache.match(req.url)) || Response.error();
  }
}

async function currentCache() {
  if (currentBuild) return caches.open(cacheName(currentBuild));
  const keys = (await caches.keys()).filter((k) => k.startsWith(`podium-${slug}-`) && k !== remoteCacheName).sort();
  return keys.length ? caches.open(keys[keys.length - 1]) : caches.open(cacheName('pending'));
}

async function navigation(req) {
  const cache = await currentCache();
  try {
    const res = await fetch(req);
    if (res.ok && res.headers.get('content-type')?.includes('text/html')) {
      const build = res.headers.get('x-podium-build');
      const target = build && build !== currentBuild ? await caches.open(cacheName(build)) : cache;
      if (build && build !== currentBuild) { currentBuild = build; dropOtherBuilds(build); }
      target.put(scopePath, stripVary(res.clone()));
    }
    return res;
  } catch {
    return (await cache.match(scopePath)) || (await caches.match(scopePath)) || Response.error();
  }
}

async function cacheFirst(req) {
  const cache = await currentCache();
  const hit = await cache.match(req, { ignoreSearch: false });
  if (hit) return hit;
  const res = await fetch(req);
  if (res.ok) cache.put(req, res.clone());
  return res;
}

async function networkFirst(req) {
  const cache = await currentCache();
  try {
    const res = await fetch(req);
    if (res.ok) cache.put(req, stripVary(res.clone()));
    return res;
  } catch {
    return (await cache.match(req)) || Response.error();
  }
}
