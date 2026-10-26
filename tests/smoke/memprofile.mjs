// Memory profile of the web app under the things people actually do with it, in one browser against a running
// Podium (memory storage, dev login enabled): a library of 112 decks, deck pages and share links, presenting to a
// room through the relay with the phone remote, a live session with an audience that reacts, asks and votes, and
// the quiet after everyone left. Normal scenarios use the GC's natural schedule. Force a collection only at the
// baseline and end to compare retained heaps; MEMPROFILE_COLLECT=1 enables diagnostic collection at every step.
// Usage: node tests/smoke/memprofile.mjs <base> [viewers=20] [audience=20] [loops=3]   (MEMPROFILE_OUT=path writes JSON)
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';

const here = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(here, '../../builder/package.json'));
const { chromium } = require('playwright-chromium');
const base = process.argv[2] || 'http://localhost:5199';
const VIEWERS = Number(process.argv[3] || 20), AUDIENCE = Number(process.argv[4] || 20), LOOPS = Number(process.argv[5] || 3);
assert(Number.isInteger(VIEWERS) && VIEWERS > 0 && VIEWERS <= 100, 'viewers must be 1..100');
assert(Number.isInteger(AUDIENCE) && AUDIENCE > 0 && AUDIENCE <= 50, 'audience must be 1..50 (room question rate limit)');
assert(Number.isInteger(LOOPS) && LOOPS > 0 && LOOPS <= 10, 'loops must be 1..10');
const collectEachStep = process.env.MEMPROFILE_COLLECT === '1';
const out = process.env.MEMPROFILE_OUT || '';
const rows = [];
const browser = await chromium.launch();
try {
const errors = [];
let client = 0;
async function context(options = {}) {
  // Separate cookies/storage and forwarded IPs: each participant is independent, not another tab of the same
  // voter. The dev server trusts forwarded headers; this avoids its deliberate per-IP anti-abuse client cap.
  const ctx = await browser.newContext({ extraHTTPHeaders: { 'X-Forwarded-For': `198.18.0.${++client}` }, ...options });
  ctx.on('page', (p) => p.on('pageerror', (e) => errors.push(`${new URL(p.url()).pathname}: ${e.message}`)));
  return ctx;
}
const owner = await context({ viewport: { width: 1400, height: 1000 } });
const op = await owner.newPage();
const checked = (r) => { assert(r.ok(), `${r.request?.()?.url?.() || r.url()}: HTTP ${r.status()}`); return r; };
const mem = async (collect) => checked(await op.request.get(`${base}/dev-mem?collect=${collect ? 'true' : 'false'}`)).json();
async function sample(scenario, note = '', collect = collectEachStep) {
  const before = await mem(false);
  const after = collect ? await mem(true) : before;
  const row = { scenario, note, collected: collect, ws: Math.max(before.workingSetMb, after.workingSetMb), wsAfter: after.workingSetMb, priv: Math.max(before.privateMb, after.privateMb), heapBefore: before.heapMb, heapAfter: after.heapMb, committed: Math.max(before.committedMb, after.committedMb), gen2: after.gen2, threads: after.threads, limit: after.heapHardLimitMb };
  rows.push(row);
  console.log(`${scenario.padEnd(34)} ws ${String(row.ws).padStart(6)} MiB  heap ${String(row.heapBefore).padStart(6)} -> ${String(row.heapAfter).padStart(6)} (${collect ? 'forced GC' : 'last GC'})  committed ${String(row.committed).padStart(6)}  gen2 ${String(row.gen2).padStart(3)}  thr ${row.threads}  ${note}`);
  return row;
}

await op.goto(`${base}/dev-login?returnUrl=/`, { waitUntil: 'networkidle' });
await sample('0 fresh process (signed in)');
const seed = await op.request.post(`${base}/dev-seed?bulk=110`, { headers: { 'x-podium-request': '1' } });
if (!seed.ok()) throw new Error(`seed ${seed.status()}`);
await sample('1 seeded: 112 decks, 38 talks', 'retained-heap baseline', true);

// Library: the heaviest page, several times, with the interactions that re-render it, and the search APIs.
for (let i = 0; i < 5; i++) {
  await op.goto(`${base}/`, { waitUntil: 'networkidle' });
  await op.click('label[for="density-list"]'); await op.waitForTimeout(100);
  await op.click('label[for="density-talks"]'); await op.waitForTimeout(100);
  await op.selectOption('#group', 'talk'); await op.waitForTimeout(100);
  await op.selectOption('#group', 'repo'); await op.click('label[for="density-grid"]');
}
for (const q of ['agents', 'sync relay', 'minimal', 'production']) {
  checked(await op.request.get(`${base}/api/search?q=${encodeURIComponent(q)}`, { headers: { 'x-podium-request': '1' } }));
  checked(await op.request.get(`${base}/api/talks/search?q=${encodeURIComponent(q)}`, { headers: { 'x-podium-request': '1' } }));
}
await sample('2 library x5 + search x8');

// Deck pages (all tabs), share links, anonymous access through a link, exports endpoints, talks, gallery, compare.
for (let i = 1; i <= 10; i++) await op.goto(`${base}/decks/load-deck-${String(i).padStart(3, '0')}`, { waitUntil: 'networkidle' });
checked(await op.goto(`${base}/decks/fixture-deck`, { waitUntil: 'networkidle' }));
for (const tab of ['share', 'analytics', 'build']) {
  await op.click(`.tab[data-tab="${tab}"]`);
  assert(await op.locator(`.tab-panel[data-tab="${tab}"]`).isVisible(), `${tab} tab is visible`);
}
const links = [];
for (let i = 0; i < 3; i++) {
  const r = await op.request.post(`${base}/api/decks/fixture-deck/links`, { headers: { 'x-podium-request': '1' }, data: { label: `profile ${i}`, artifact: 'Site', expiresInDays: 7 } });
  links.push((await checked(r).json()).link.id);
}
checked(await op.request.patch(`${base}/api/decks/fixture-deck`, { headers: { 'x-podium-request': '1' }, data: { visibility: 'Private' } }));
for (const id of links) {
  const guest = await context();
  const page = await guest.newPage();
  checked(await page.goto(`${base}/d/fixture-deck/?share=${id}`, { waitUntil: 'load' }));
  await page.waitForFunction(() => window.__podium?.connected);
  await guest.close();
}
checked(await op.request.patch(`${base}/api/decks/fixture-deck`, { headers: { 'x-podium-request': '1' }, data: { visibility: 'Public' } }));
const anonA = await context(); const ap = await anonA.newPage();
await ap.goto(`${base}/gallery`, { waitUntil: 'networkidle' });
await ap.goto(`${base}/talks`, { waitUntil: 'networkidle' });
await ap.goto(`${base}/talks/fixture-slides-fixture`, { waitUntil: 'networkidle' });
await op.goto(`${base}/talks`, { waitUntil: 'networkidle' });
await op.goto(`${base}/talks/fixture-slides-fixture`, { waitUntil: 'networkidle' });
checked(await op.request.get(`${base}/talks/fixture-slides-fixture/cfp.md`));
await op.goto(`${base}/decks/fixture-deck/compare/fixture-deck-workshop`, { waitUntil: 'networkidle' });
await op.goto(`${base}/activity`, { waitUntil: 'networkidle' });
await op.goto(`${base}/sources`, { waitUntil: 'networkidle' });
await sample('3 deck pages, links, talks, compare', `${links.filter(Boolean).length} links`);

// Presenting: the owner presents, VIEWERS anonymous windows follow through the relay, the phone remote drives.
const presenter = await owner.newPage();
await presenter.goto(`${base}/d/fixture-deck/1`, { waitUntil: 'networkidle' });
await presenter.waitForFunction(() => window.__podium?.connected, null, { timeout: 10000 });
const viewers = [];
for (let i = 0; i < VIEWERS; i++) {
  const v = await (await context()).newPage();
  checked(await v.goto(`${base}/d/fixture-deck/1`, { waitUntil: 'load' }));
  viewers.push(v);
}
await Promise.all(viewers.map((v) => v.waitForFunction(() => window.__podium?.connected, null, { timeout: 15000 })));
const remote = await (await context({ viewport: { width: 420, height: 860 }, storageState: await owner.storageState() })).newPage();
await remote.goto(`${base}/d/fixture-deck/remote`, { waitUntil: 'networkidle' }); await remote.waitForTimeout(800);
await sample(`4 presenting to ${VIEWERS} viewers + remote`, 'connections open');

async function driveLoop() {
  for (let i = 0; i < 6; i++) { await remote.click('#next'); await remote.waitForTimeout(120); }
  for (let i = 0; i < 6; i++) { await remote.click('#prev'); await remote.waitForTimeout(120); }
  await remote.click('#black'); await remote.waitForTimeout(200); await remote.click('#black');
  await remote.click('#laser');
  const box = await remote.locator('#stage-current').boundingBox();
  assert(box, 'remote laser pad is visible');
    await remote.mouse.move(box.x + box.width * 0.2, box.y + box.height * 0.3); await remote.mouse.down();
    for (let s = 0; s < 10; s++) await remote.mouse.move(box.x + box.width * (0.2 + s * 0.06), box.y + box.height * (0.3 + s * 0.04), { steps: 3 });
    await remote.mouse.up();
  await remote.click('#laser');
  for (let i = 0; i < 10; i++) { await presenter.keyboard.press('ArrowRight'); await presenter.waitForTimeout(60); }
  for (let i = 0; i < 10; i++) { await presenter.keyboard.press('ArrowLeft'); await presenter.waitForTimeout(60); }
  await Promise.all(viewers.map((v) => v.waitForFunction(() => window.__podium?.position().page === 1, null, { timeout: 8000 })));
}
for (let loop = 1; loop <= LOOPS; loop++) { await driveLoop(); await sample(`5.${loop} drive loop (nav, laser, blackout)`, `loop ${loop} of ${LOOPS}`); }

// Live session with a room: AUDIENCE people join through the code, react, ask and vote; two polls; the session ends.
checked(await op.request.patch(`${base}/api/decks/fixture-deck`, { headers: { 'x-podium-request': '1' }, data: { visibility: 'Private' } }));
const sess = await op.request.post(`${base}/api/decks/fixture-deck/sessions`, { headers: { 'x-podium-request': '1' }, data: { plannedMinutes: 45, holdDeploys: false, freeze: true } });
if (!sess.ok()) throw new Error(`session ${sess.status()}`);
await remote.reload({ waitUntil: 'networkidle' }); await remote.waitForTimeout(1000);
const code = (await remote.locator('.join-code').textContent()).trim();
assert.match(code, /^[A-Z2-9]{3}-[A-Z2-9]{3}$/);
const room = [];
for (let i = 0; i < AUDIENCE; i++) {
  const j = await (await context()).newPage();
  checked(await j.goto(`${base}/j/${code.toLowerCase()}`, { waitUntil: 'load' }));
  room.push(j);
}
await Promise.all(room.map((j) => j.waitForFunction(() => window.__podium?.connected, null, { timeout: 15000 })));
await Promise.all(room.map((j) => j.waitForSelector('#podium-audience:not([hidden])', { timeout: 8000 })));
await sample(`6 live session, ${AUDIENCE} in the room`, `code ${code}`);
for (const j of room) { for (let k = 0; k < 3; k++) await j.click('.podium-react[data-kind="clap"]'); }
await remote.waitForFunction((n) => Number(document.querySelector('#aud-totals')?.textContent.match(/\d+/)?.[0]) === n, AUDIENCE * 3, { timeout: 15000 });
for (const [i, j] of room.entries()) {
    await j.click('#podium-audience .podium-abtn:not([hidden])');
    await j.waitForSelector('#podium-sheet textarea', { timeout: 3000 });
    await j.fill('#podium-sheet textarea', `Question number ${i} about memory and the remote?`);
    await j.fill('#podium-sheet input[type="text"]', `Person ${i}`);
    await j.click('#podium-sheet button[type="submit"]');
    await j.keyboard.press('Escape');
}
await remote.waitForFunction((n) => document.querySelectorAll('#aud-questions .aud-q').length === n, AUDIENCE, { timeout: 15000 });
for (const q of ['Which editor?', 'Coffee or tea?']) {
  await remote.click('#aud-newpoll'); await remote.fill('#poll-question', q); await remote.click('#poll-form [data-preset="yesno"]'); await remote.click('#poll-form button[value="create"]');
  await Promise.all(room.map(async (j, i) => {
    await j.waitForFunction((q) => document.querySelector('#podium-sheet')?.textContent.includes(q), q, { timeout: 10000 });
    await j.click(`#podium-sheet .podium-option >> nth=${i % 2}`);
    await j.click('#podium-sheet button:has-text("Vote for")');
  }));
  await remote.waitForFunction((n) => document.querySelector('#aud-poll .aud-poll-head .faint')?.textContent.startsWith(`${n} vote`), AUDIENCE, { timeout: 15000 });
  await remote.click('#aud-poll button:has-text("Show on screen")');
  await remote.waitForTimeout(300);
}
await sample(`7 reactions x${AUDIENCE * 3}, ${AUDIENCE} questions, 2 polls`);
remote.once('dialog', (d) => d.accept());
await remote.click('#session .danger');
await Promise.all(room.map((j) => j.waitForSelector('#podium-ended', { timeout: 8000 })));
checked(await op.request.patch(`${base}/api/decks/fixture-deck`, { headers: { 'x-podium-request': '1' }, data: { visibility: 'Public' } }));
await sample('8 session ended, room still connected');

// Everyone leaves: the server should come back down to roughly where it started (plus caches).
for (const j of room) await j.context().close();
for (const v of viewers) await v.context().close();
await presenter.close(); await remote.context().close(); await anonA.close();
await op.waitForTimeout(3000);
await sample('9 everyone left (3 s later)');
await op.waitForTimeout(5000);
await sample('10 idle 8 s after', 'retained-heap comparison', true);

const summary = { viewers: VIEWERS, audience: AUDIENCE, loops: LOOPS, collectEachStep, rows, errors, peakWorkingSetMb: Math.max(...rows.map((r) => r.ws)), peakHeapSnapshotMb: Math.max(...rows.flatMap((r) => [r.heapBefore, r.heapAfter])), heapHardLimitMb: rows[0]?.limit ?? null };
if (out) writeFileSync(out, JSON.stringify(summary, null, 1));
console.log(`sampled peak working set ${summary.peakWorkingSetMb} MiB, largest last-GC heap ${summary.peakHeapSnapshotMb} MiB, GC hard limit ${summary.heapHardLimitMb ?? 'none'} MiB`);
console.log(`errors ${JSON.stringify(errors)}`);
// A leak would show as the live heap after everyone left exceeding the seeded baseline by a wide margin.
const baseline = rows[1]?.heapAfter ?? rows[0].heapAfter, final = rows[rows.length - 1].heapAfter;
assert(final <= baseline * 2 + 20, `retained heap grew: ${baseline} MiB after seeding, ${final} MiB at the end`);
assert.equal(errors.length, 0, 'page errors');
} finally { await browser.close(); }
