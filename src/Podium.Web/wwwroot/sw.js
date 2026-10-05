// Podium app-shell service worker (scope "/"). Makes Podium installable and keeps the UI's static assets cached.
// It deliberately handles only versioned static files: never pages (they depend on who is signed in), never the API
// or sockets, and never deck content (decks have their own worker scoped to /d/<slug>/, which wins there).
'use strict';
const CACHE = 'podium-shell-v1';
const SHELL = /^\/(css|js|icons)\/|^\/favicon\.svg$|^\/manifest\.webmanifest$/;

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(
  caches.keys().then((keys) => Promise.all(keys.filter((k) => k.startsWith('podium-shell-') && k !== CACHE).map((k) => caches.delete(k)))).then(() => self.clients.claim())
));

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin || !SHELL.test(url.pathname)) return;
  // Versioned (?v=hash) assets never change: cache first. Everything else: network first, cache as the offline fallback.
  e.respondWith(url.searchParams.has('v') ? cacheFirst(req) : networkFirst(req));
});

async function cacheFirst(req) {
  const cache = await caches.open(CACHE);
  const hit = await cache.match(req);
  if (hit) return hit;
  const res = await fetch(req);
  if (res.ok) cache.put(req, res.clone());
  return res;
}

async function networkFirst(req) {
  const cache = await caches.open(CACHE);
  try {
    const res = await fetch(req);
    if (res.ok) cache.put(req, res.clone());
    return res;
  } catch {
    return (await cache.match(req)) || Response.error();
  }
}