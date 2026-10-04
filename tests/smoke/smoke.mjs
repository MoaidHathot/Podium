// Browser smoke test for CI: boots against a running Podium (memory storage, dev login enabled), seeds the fixture
// deck, and drives the library, details, deck page, remote and the sync relay in real Chromium.
// Usage: node tests/smoke/smoke.mjs http://localhost:5187
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(here, '../../builder/package.json'));
const { chromium } = require('playwright-chromium');

const base = process.argv[2] || 'http://localhost:5187';
const failures = [];
const check = (name, ok, detail = '') => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` - ${detail}` : ''}`); if (!ok) failures.push(name); };

const browser = await chromium.launch();
try {
  // Owner context (dev login) + anonymous context.
  const owner = await browser.newContext({ viewport: { width: 1400, height: 1000 } });
  const op = await owner.newPage();
  const errors = [];
  op.on('pageerror', (e) => errors.push(`owner: ${e.message}`));
  op.on('console', (m) => { if (m.type() === 'error') errors.push(`owner console: ${m.text()}`); });
  await op.addInitScript(() => document.addEventListener('securitypolicyviolation', (e) => console.error(`CSP violation: ${e.violatedDirective} ${e.blockedURI}`)));
  await op.goto(`${base}/dev-login?returnUrl=/`, { waitUntil: 'networkidle' });
  const cookies = await owner.cookies(base);
  const session = cookies.find((c) => c.name === 'podium_session');
  check('owner signed in', !!session);
  const seed = await op.request.post(`${base}/dev-seed`, { headers: { 'x-podium-request': '1' } });
  check('fixture seeded', seed.ok(), String(seed.status()));

  await op.goto(`${base}/`, { waitUntil: 'networkidle' });
  check('library renders fixture card', (await op.locator('.card[data-slug="fixture-deck"]').count()) > 0);
  await op.fill('#search', 'slide two'); await op.waitForTimeout(600);
  check('full-text search hits slide 2', (await op.locator('#slide-hits-list li a[href$="/fixture-deck/2"]').count()) > 0);

  await op.goto(`${base}/decks/fixture-deck`, { waitUntil: 'networkidle' });
  check('details renders', (await op.locator('h3:has-text("Present")').count()) > 0);
  check('details go-live panel', (await op.locator('#session-start').count()) > 0);

  // Deck page as presenter (owner) and as an anonymous viewer; relay must drive the viewer.
  const presenter = await owner.newPage();
  presenter.on('pageerror', (e) => errors.push(`presenter: ${e.message}`));
  await presenter.goto(`${base}/d/fixture-deck/1`, { waitUntil: 'networkidle' });
  await presenter.waitForFunction(() => document.body.dataset.canSend === 'true', null, { timeout: 10000 }).catch(() => {});
  check('presenter socket may send', (await presenter.evaluate(() => document.body.dataset.canSend)) === 'true');
  check('live.js injected', await presenter.evaluate(() => !!document.querySelector('script[src*="/_podium/live.js"]')));

  const anon = await browser.newContext();
  const vp = await anon.newPage();
  vp.on('pageerror', (e) => errors.push(`viewer: ${e.message}`));
  await vp.goto(`${base}/d/fixture-deck/1`, { waitUntil: 'networkidle' });
  await vp.waitForTimeout(800);
  check('viewer socket is read-only', (await vp.evaluate(() => document.body.dataset.canSend)) === 'false');
  await presenter.keyboard.press('ArrowRight'); await presenter.keyboard.press('ArrowRight'); await vp.waitForTimeout(1000);
  check('viewer follows presenter to slide 3', (await vp.locator('#slide').innerText()) === '3', await vp.locator('#slide').innerText());

  // Remote: notes and navigation.
  const remote = await owner.newPage({ viewport: { width: 420, height: 860 } });
  remote.on('pageerror', (e) => errors.push(`remote: ${e.message}`));
  await remote.goto(`${base}/d/fixture-deck/remote`, { waitUntil: 'networkidle' }); await remote.waitForTimeout(1200);
  check('remote shows position', (await remote.locator('#page').innerText()) === '3', await remote.locator('#page').innerText());
  check('remote shows notes', await remote.locator('#notes').isVisible());
  await remote.click('#prev'); await remote.waitForTimeout(900);
  check('remote prev moves presenter + viewer', (await presenter.locator('#slide').innerText()) === '2' && (await vp.locator('#slide').innerText()) === '2');
  await remote.click('#black'); await remote.waitForTimeout(700);
  check('blackout reaches viewer only', (await vp.locator('#screen').count()) === 1 && (await presenter.locator('#screen').count()) === 0);
  await remote.click('#black'); await remote.waitForTimeout(500);

  // Anonymous access rules.
  const r1 = await vp.request.get(`${base}/d/fixture-deck/notes.json`);
  check('notes hidden from viewers', r1.status() === 404, String(r1.status()));
  const r2 = await vp.request.get(`${base}/api/decks`);
  check('owner API needs auth', r2.status() === 401, String(r2.status()));
  const gallery = await vp.goto(`${base}/`, { waitUntil: 'networkidle' });
  check('anonymous root -> gallery', new URL(vp.url()).pathname === '/gallery', vp.url());
  check('gallery lists public fixture', (await vp.locator('.card').count()) >= 1);

  check('no page errors / CSP violations', errors.length === 0, errors.join(' | '));
} finally { await browser.close(); }

if (failures.length) { console.error(`\n${failures.length} check(s) failed: ${failures.join(', ')}`); process.exit(1); }
console.log('\nsmoke OK');
