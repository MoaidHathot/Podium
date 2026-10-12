// Memory profile of the web app under the things people actually do with it, in one browser against a running
// Podium (memory storage, dev login enabled): a library of 112 decks, deck pages and share links, presenting to a
// room through the relay with the phone remote, a live session with an audience that reacts, asks and votes, and
// the quiet after everyone left. After each scenario it prints the process memory as the runtime sees it (/dev-mem),
// before and after a forced full GC, so a leak shows up as a live heap that does not come back down.
// Usage: node tests/smoke/memprofile.mjs <base> [viewers=20] [audience=20] [loops=3]   (MEMPROFILE_OUT=path writes JSON)
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { writeFileSync } from 'node:fs';

const here = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(here, '../../builder/package.json'));
const { chromium } = require('playwright-chromium');
const base = process.argv[2] || 'http://localhost:5199';
const VIEWERS = Number(process.argv[3] || 20), AUDIENCE = Number(process.argv[4] || 20), LOOPS = Number(process.argv[5] || 3);
const out = process.env.MEMPROFILE_OUT || '';
const rows = [];
const browser = await chromium.launch();
const owner = await browser.newContext({ viewport: { width: 1400, height: 1000 } });
const op = await owner.newPage();
const errors = [];
op.on('pageerror', (e) => errors.push(`owner: ${e.message}`));
const mem = async (collect) => (await (await op.request.get(`${base}/dev-mem?collect=${collect ? 'true' : 'false'}`)).json());
async function sample(scenario, note = '') {
  const before = await mem(false);
  const after = await mem(true);
  const row = { scenario, note, ws: after.workingSetMb, priv: after.privateMb, heapBefore: before.heapMb, heapAfter: after.heapMb, committed: after.committedMb, gen2: after.gen2, threads: after.threads, limit: after.heapHardLimitMb };
  rows.push(row);
  console.log(`${scenario.padEnd(34)} ws ${String(row.ws).padStart(6)} MB  priv ${String(row.priv).padStart(6)}  heap ${String(row.heapBefore).padStart(6)} -> ${String(row.heapAfter).padStart(6)} (live)  committed ${String(row.committed).padStart(6)}  gen2 ${String(row.gen2).padStart(3)}  thr ${row.threads}  ${note}`);
  return row;
}

await op.goto(`${base}/dev-login?returnUrl=/`, { waitUntil: 'networkidle' });
await sample('0 fresh process (signed in)');
const seed = await op.request.post(`${base}/dev-seed?bulk=110`, { headers: { 'x-podium-request': '1' } });
if (!seed.ok()) throw new Error(`seed ${seed.status()}`);
await sample('1 seeded: 112 decks, 38 talks');

// Library: the heaviest page, several times, with the interactions that re-render it, and the search APIs.
for (let i = 0; i < 5; i++) {
  await op.goto(`${base}/`, { waitUntil: 'networkidle' });
  await op.click('label[for="density-list"]'); await op.waitForTimeout(100);
  await op.click('label[for="density-talks"]'); await op.waitForTimeout(100);
  await op.selectOption('#group', 'talk'); await op.waitForTimeout(100);
  await op.selectOption('#group', 'repo'); await op.click('label[for="density-grid"]');
}
for (const q of ['agents', 'sync relay', 'minimal', 'production']) {
  await op.request.get(`${base}/api/search?q=${encodeURIComponent(q)}`, { headers: { 'x-podium-request': '1' } });
  await op.request.get(`${base}/api/talks/search?q=${encodeURIComponent(q)}`, { headers: { 'x-podium-request': '1' } });
}
await sample('2 library x5 + search x8');

// Deck pages (all tabs), share links, anonymous access through a link, exports endpoints, talks, gallery, compare.
for (let i = 1; i <= 10; i++) await op.goto(`${base}/decks/load-deck-${String(i).padStart(3, '0')}`, { waitUntil: 'networkidle' });
for (const tab of ['share', 'analytics', 'build']) await op.goto(`${base}/decks/fixture-deck?tab=${tab}`, { waitUntil: 'networkidle' });
const links = [];
for (let i = 0; i < 3; i++) {
  const r = await op.request.post(`${base}/api/decks/fixture-deck/links`, { headers: { 'x-podium-request': '1' }, data: { label: `profile ${i}`, artifact: 'Site', expiresInDays: 7 } });
  if (r.ok()) links.push((await r.json()).link.id);
}
const anonA = await browser.newContext(); const ap = await anonA.newPage();
for (const id of links.filter(Boolean)) { await ap.goto(`${base}/d/fixture-deck/?share=${id}`, { waitUntil: 'load' }); await ap.waitForTimeout(200); }
await ap.goto(`${base}/gallery`, { waitUntil: 'networkidle' });
await ap.goto(`${base}/talks`, { waitUntil: 'networkidle' });
await ap.goto(`${base}/talks/fixture-slides-fixture`, { waitUntil: 'networkidle' });
await op.goto(`${base}/talks`, { waitUntil: 'networkidle' });
await op.goto(`${base}/talks/fixture-slides-fixture`, { waitUntil: 'networkidle' });
await op.request.get(`${base}/talks/fixture-slides-fixture/cfp.md`);
await op.goto(`${base}/decks/fixture-deck/compare/fixture-deck-workshop`, { waitUntil: 'networkidle' });
await op.goto(`${base}/activity`, { waitUntil: 'networkidle' });
await op.goto(`${base}/sources`, { waitUntil: 'networkidle' });
await sample('3 deck pages, links, talks, compare', `${links.filter(Boolean).length} links`);

// Presenting: the owner presents, VIEWERS anonymous windows follow through the relay, the phone remote drives.
const presenter = await owner.newPage();
presenter.on('pageerror', (e) => errors.push(`presenter: ${e.message}`));
await presenter.goto(`${base}/d/fixture-deck/1`, { waitUntil: 'networkidle' });
await presenter.waitForFunction(() => window.__podium && window.__podium.connected, null, { timeout: 10000 }).catch(() => {});
const viewersCtx = await browser.newContext();
const viewers = [];
for (let i = 0; i < VIEWERS; i++) {
  const v = await viewersCtx.newPage();
  await v.goto(`${base}/d/fixture-deck/1`, { waitUntil: 'load' });
  viewers.push(v);
}
await Promise.all(viewers.map((v) => v.waitForFunction(() => window.__podium && window.__podium.connected, null, { timeout: 15000 }).catch(() => {})));
const remote = await owner.newPage({ viewport: { width: 420, height: 860 } });
remote.on('pageerror', (e) => errors.push(`remote: ${e.message}`));
await remote.goto(`${base}/d/fixture-deck/remote`, { waitUntil: 'networkidle' }); await remote.waitForTimeout(800);
await sample(`4 presenting to ${VIEWERS} viewers + remote`, 'connections open');

async function driveLoop() {
  for (let i = 0; i < 6; i++) { await remote.click('#next'); await remote.waitForTimeout(120); }
  for (let i = 0; i < 6; i++) { await remote.click('#prev'); await remote.waitForTimeout(120); }
  await remote.click('#black'); await remote.waitForTimeout(200); await remote.click('#black');
  await remote.click('#laser');
  const box = await remote.locator('#stage-current').boundingBox();
  if (box) {
    await remote.mouse.move(box.x + box.width * 0.2, box.y + box.height * 0.3); await remote.mouse.down();
    for (let s = 0; s < 10; s++) await remote.mouse.move(box.x + box.width * (0.2 + s * 0.06), box.y + box.height * (0.3 + s * 0.04), { steps: 3 });
    await remote.mouse.up();
  }
  await remote.click('#laser');
  for (let i = 0; i < 10; i++) { await presenter.keyboard.press('ArrowRight'); await presenter.waitForTimeout(60); }
  for (let i = 0; i < 10; i++) { await presenter.keyboard.press('ArrowLeft'); await presenter.waitForTimeout(60); }
}
for (let loop = 1; loop <= LOOPS; loop++) { await driveLoop(); await sample(`5.${loop} drive loop (nav, laser, blackout)`, `loop ${loop} of ${LOOPS}`); }

// Live session with a room: AUDIENCE people join through the code, react, ask and vote; two polls; the session ends.
await op.request.patch(`${base}/api/decks/fixture-deck`, { headers: { 'x-podium-request': '1' }, data: { visibility: 'Private' } });
const sess = await op.request.post(`${base}/api/decks/fixture-deck/sessions`, { headers: { 'x-podium-request': '1' }, data: { plannedMinutes: 45, holdDeploys: false, freeze: true } });
if (!sess.ok()) throw new Error(`session ${sess.status()}`);
await remote.reload({ waitUntil: 'networkidle' }); await remote.waitForTimeout(1000);
const code = ((await remote.locator('.join-code').textContent().catch(() => '')) || '').trim();
const roomCtx = await browser.newContext();
const room = [];
for (let i = 0; i < AUDIENCE; i++) {
  const j = await roomCtx.newPage();
  await j.goto(`${base}/j/${code.toLowerCase()}`, { waitUntil: 'load' });
  room.push(j);
}
await Promise.all(room.map((j) => j.waitForFunction(() => window.__podium && window.__podium.connected, null, { timeout: 15000 }).catch(() => {})));
await Promise.all(room.map((j) => j.waitForSelector('#podium-audience:not([hidden])', { timeout: 8000 }).catch(() => {})));
await sample(`6 live session, ${AUDIENCE} in the room`, `code ${code}`);
for (const j of room) { for (let k = 0; k < 3; k++) await j.click('.podium-react[data-kind="clap"]').catch(() => {}); }
for (const [i, j] of room.entries()) {
  try {
    await j.click('#podium-audience .podium-abtn:not([hidden])');
    await j.waitForSelector('#podium-sheet textarea', { timeout: 3000 });
    await j.fill('#podium-sheet textarea', `Question number ${i} about memory and the remote?`);
    await j.fill('#podium-sheet input[type="text"]', `Person ${i}`);
    await j.click('#podium-sheet button[type="submit"]');
    await j.keyboard.press('Escape');
  } catch { }
}
for (const q of ['Which editor?', 'Coffee or tea?']) {
  await remote.click('#aud-newpoll'); await remote.fill('#poll-question', q); await remote.click('#poll-form [data-preset="yesno"]'); await remote.click('#poll-form button[value="create"]');
  await Promise.all(room.map(async (j, i) => { try { await j.waitForSelector('#podium-sheet .podium-option', { timeout: 5000 }); await j.click(`#podium-sheet .podium-option >> nth=${i % 2}`); await j.click('#podium-sheet button:has-text("Vote for")', { timeout: 2000 }).catch(() => {}); } catch { } }));
  await remote.click('#aud-poll button:has-text("Show on screen")').catch(() => {});
  await remote.waitForTimeout(300);
}
await sample(`7 reactions x${AUDIENCE * 3}, ${AUDIENCE} questions, 2 polls`);
remote.once('dialog', (d) => d.accept());
await remote.click('#session .danger');
await Promise.all(room.map((j) => j.waitForSelector('#podium-ended', { timeout: 8000 }).catch(() => {})));
await op.request.patch(`${base}/api/decks/fixture-deck`, { headers: { 'x-podium-request': '1' }, data: { visibility: 'Public' } });
await sample('8 session ended, room still connected');

// Everyone leaves: the server should come back down to roughly where it started (plus caches).
for (const j of room) await j.close(); await roomCtx.close();
for (const v of viewers) await v.close(); await viewersCtx.close();
await presenter.close(); await remote.close(); await anonA.close();
await op.waitForTimeout(3000);
await sample('9 everyone left (3 s later)');
await op.waitForTimeout(5000);
await sample('10 idle 8 s after');

const summary = { viewers: VIEWERS, audience: AUDIENCE, loops: LOOPS, rows, errors, peakWorkingSetMb: Math.max(...rows.map((r) => r.ws)), peakLiveHeapMb: Math.max(...rows.map((r) => r.heapAfter)), heapHardLimitMb: rows[0]?.limit ?? null };
if (out) writeFileSync(out, JSON.stringify(summary, null, 1));
console.log(`peak working set ${summary.peakWorkingSetMb} MB, peak live heap ${summary.peakLiveHeapMb} MB, GC hard limit ${summary.heapHardLimitMb ?? 'none'} MB`);
console.log(`errors ${JSON.stringify(errors)}`);
await browser.close();
// A leak would show as the live heap after everyone left exceeding the seeded baseline by a wide margin.
const baseline = rows[1]?.heapAfter ?? rows[0].heapAfter, final = rows[rows.length - 1].heapAfter;
if (final > baseline * 2 + 20) { console.error(`live heap did not come back down: ${baseline} MB after seeding, ${final} MB at the end`); process.exit(1); }
if (errors.length) { console.error('page errors'); process.exit(1); }
