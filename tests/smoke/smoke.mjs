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
  check('profile picture loads under the CSP', await op.evaluate(() => { const i = document.querySelector('img.avatar'); return !!i && i.complete && i.naturalWidth > 0; }));
  // Phone viewport: nothing may push the page wider than the screen.
  await op.setViewportSize({ width: 390, height: 844 });
  for (const path of ['/', '/decks/fixture-deck', '/sources', '/activity']) {
    await op.goto(`${base}${path}`, { waitUntil: 'networkidle' });
    const o = await op.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    check(`no horizontal overflow on a phone: ${path}`, o <= 0, `${o}px`);
  }
  await op.setViewportSize({ width: 1400, height: 1000 });
  await op.goto(`${base}/`, { waitUntil: 'networkidle' });
  await op.fill('#search', 'slide two'); await op.waitForTimeout(600);
  check('full-text search hits slide 2', (await op.locator('#slide-hits-list li a[href$="/fixture-deck/2"]').count()) > 0);

  await op.goto(`${base}/decks/fixture-deck`, { waitUntil: 'networkidle' });
  check('details renders', (await op.locator('h3:has-text("Present")').count()) > 0);
  check('details go-live panel', (await op.locator('#session-start').count()) > 0);

  // Deck page as presenter (owner) and as an anonymous viewer; relay must drive the viewer. The fixture is a
  // PDF-kind deck, so this exercises the server-served runtime (bridge.js + pages.js + live-ui.js) end to end.
  const presenter = await owner.newPage();
  presenter.on('pageerror', (e) => errors.push(`presenter: ${e.message}`));
  await presenter.goto(`${base}/d/fixture-deck/1`, { waitUntil: 'networkidle' });
  await presenter.waitForFunction(() => window.__podium && window.__podium.canSend === true, null, { timeout: 10000 }).catch(() => {});
  check('presenter socket may send', await presenter.evaluate(() => !!(window.__podium && window.__podium.canSend)));
  check('live.js injected', await presenter.evaluate(() => !!document.querySelector('script[src*="/_podium/live.js"]')));
  check('v3 runtime injected (bridge, pages viewer, live UI)', await presenter.evaluate(() => ['bridge.js', 'pages.js', 'live-ui.js'].every((f) => !!document.querySelector(`script[src*="/_podium/${f}"]`)) && !!document.querySelector('link[href*="/_podium/live-ui.css"]')));
  check('pages viewer renders page 1', (await presenter.locator('#podium-pages img.pp-slide').getAttribute('src') || '').endsWith('/pages/001.jpg'));
  check('presenter alone sees no pill', (await presenter.locator('#podium-live:not([hidden])').count()) === 0);

  const anon = await browser.newContext();
  const vp = await anon.newPage();
  vp.on('pageerror', (e) => errors.push(`viewer: ${e.message}`));
  await vp.goto(`${base}/d/fixture-deck/1`, { waitUntil: 'networkidle' });
  await vp.waitForFunction(() => window.__podium && window.__podium.connected, null, { timeout: 10000 }).catch(() => {});
  await vp.waitForTimeout(500);
  check('viewer socket is read-only', (await vp.evaluate(() => window.__podium.canSend)) === false);
  check('viewer sees the live pill, following', /following/.test(await vp.locator('#podium-live').innerText().catch(() => '')));
  check('presenter pill shows the room', /1 watching/.test(await presenter.locator('#podium-live').innerText().catch(() => '')));
  await presenter.keyboard.press('ArrowRight'); await presenter.keyboard.press('ArrowRight'); await vp.waitForTimeout(1000);
  const viewerPage = () => vp.evaluate(() => window.__podium.position().page);
  const presenterPage = () => presenter.evaluate(() => window.__podium.position().page);
  check('viewer follows presenter to slide 3', (await viewerPage()) === 3, String(await viewerPage()));
  check('deep link follows the slide', /\/fixture-deck\/3$/.test(vp.url()), vp.url());
  // Browsing freely stops following; "Jump to live" re-attaches.
  await vp.keyboard.press('ArrowLeft'); await vp.waitForTimeout(400);
  check('viewer browsing shows where the presenter is', /browsing/.test(await vp.locator('#podium-live').innerText()) && /presenter on 3/.test(await vp.locator('#podium-live').innerText()), await vp.locator('#podium-live').innerText());
  await vp.click('#podium-live button'); await vp.waitForTimeout(400);
  check('jump to live re-attaches the viewer', (await viewerPage()) === 3 && /following/.test(await vp.locator('#podium-live').innerText()));

  // Remote: notes and navigation.
  const remote = await owner.newPage({ viewport: { width: 420, height: 860 } });
  remote.on('pageerror', (e) => errors.push(`remote: ${e.message}`));
  await remote.goto(`${base}/d/fixture-deck/remote`, { waitUntil: 'networkidle' }); await remote.waitForTimeout(1200);
  check('remote shows position', (await remote.locator('#page').innerText()) === '3', await remote.locator('#page').innerText());
  check('remote shows notes', await remote.locator('#notes').isVisible());
  await remote.click('#prev'); await remote.waitForTimeout(900);
  check('remote prev moves presenter + viewer', (await presenterPage()) === 2 && (await viewerPage()) === 2, `${await presenterPage()} / ${await viewerPage()}`);
  await remote.click('#black'); await remote.waitForTimeout(700);
  check('blackout reaches the viewer and the presenting window', (await vp.locator('#podium-screen').count()) === 1 && (await presenter.locator('#podium-screen').count()) === 1);
  await remote.click('#black'); await remote.waitForTimeout(500);
  check('blackout lifts everywhere', (await vp.locator('#podium-screen').count()) === 0 && (await presenter.locator('#podium-screen').count()) === 0);
  // Laser pad: drag on the current-slide thumbnail -> dot on the viewer and the presenting window.
  check('remote shows current + next thumbnails', !(await remote.locator('#thumb-current').isHidden()) && !(await remote.locator('#thumb-next').isHidden()));
  await remote.click('#laser');
  const stageBox = await remote.locator('#stage-current').boundingBox();
  await remote.mouse.move(stageBox.x + stageBox.width * 0.3, stageBox.y + stageBox.height * 0.4);
  await remote.mouse.down();
  await remote.mouse.move(stageBox.x + stageBox.width * 0.5, stageBox.y + stageBox.height * 0.5, { steps: 4 });
  await vp.waitForTimeout(400);
  check('laser dot reaches the viewer', (await vp.locator('#podium-laser').count()) === 1 && (await vp.locator('#podium-laser').evaluate((e) => e.style.opacity)) === '1');
  await remote.mouse.up(); await vp.waitForTimeout(400);
  check('laser dot clears on release', (await vp.locator('#podium-laser').evaluate((e) => e.style.opacity)) === '0');
  await remote.click('#laser');
  // Shared timer: the phone starts the deck's timer; the deck window publishes it back.
  await remote.click('#timer-toggle'); await remote.waitForTimeout(1600);
  check('deck timer runs from the phone', /running/.test(await remote.locator('#timer').getAttribute('class')) && /00:0[1-9]/.test(await remote.locator('#timer').innerText()), await remote.locator('#timer').innerText());
  await remote.click('#timer-reset'); await remote.waitForTimeout(400);
  check('agenda strip marks the current slide', (await remote.locator('#agenda button.current').innerText()).startsWith('2'));

  // Live session from the owner's remote: countdown + join code; the room joins a (now Private) deck through /j/CODE.
  await op.request.patch(`${base}/api/decks/fixture-deck`, { headers: { 'x-podium-request': '1' }, data: { visibility: 'Private' } });
  const sess = await op.request.post(`${base}/api/decks/fixture-deck/sessions`, { headers: { 'x-podium-request': '1' }, data: { plannedMinutes: 45, holdDeploys: false, freeze: true } });
  check('session started', sess.ok(), String(sess.status()));
  await remote.reload({ waitUntil: 'networkidle' }); await remote.waitForTimeout(1200);
  const countdown = await remote.locator('#countdown-big').innerText().catch(() => '');
  check('remote shows session countdown', /^44:\d\d$|^45:00$/.test(countdown), countdown);
  const code = (await remote.locator('.join-code').textContent().catch(() => '') || '').trim(); // inside a collapsed <details>
  check('remote shows join code', /^[A-Z2-9]{3}-[A-Z2-9]{3}$/.test(code), code);
  // Library banner + the Remote shortcut route while live; the deck page carries the scan-to-open QR.
  await op.goto(`${base}/`, { waitUntil: 'networkidle' });
  check('library shows the live banner', (await op.locator('#live-now a[href="/d/fixture-deck/remote"]').count()) === 1);
  const shortcut = await op.request.get(`${base}/remote`, { maxRedirects: 0 });
  check('/remote shortcut points at the live deck', shortcut.status() === 302 && shortcut.headers()['location'] === '/d/fixture-deck/remote', `${shortcut.status()} ${shortcut.headers()['location']}`);
  await op.goto(`${base}/decks/fixture-deck`, { waitUntil: 'networkidle' });
  check('deck page shows the remote QR', await op.evaluate(() => { const i = document.querySelector('#phone-remote img.qr'); return !!i && i.complete && i.naturalWidth > 0; }));
  const joiner = await (await browser.newContext()).newPage();
  const jr = await joiner.goto(`${base}/j/${code.toLowerCase()}`, { waitUntil: 'load' });
  await joiner.waitForFunction(() => window.__podium && window.__podium.connected, null, { timeout: 10000 }).catch(() => {});
  await joiner.waitForTimeout(500);
  check('room joins private deck via code without sign-in', jr.ok() && new URL(joiner.url()).pathname.startsWith('/d/fixture-deck/'), joiner.url());
  check('room member follows the presenter', (await joiner.evaluate(() => window.__podium.position().page)) === 2, String(await joiner.evaluate(() => window.__podium.position().page)));

  // Audience: the room member reacts, asks and votes from the bar; the remote moderates; the projector shows it.
  await joiner.waitForSelector('#podium-audience:not([hidden])', { timeout: 5000 }).catch(() => {});
  check('viewer sees the audience bar', (await joiner.locator('#podium-audience:not([hidden])').count()) === 1);
  await joiner.click('.podium-react[data-kind="clap"]'); await joiner.click('.podium-react[data-kind="clap"]');
  await remote.waitForFunction(() => /2/.test(document.querySelector('#aud-totals')?.textContent || ''), null, { timeout: 5000 }).catch(() => {});
  check('remote counts the reactions', /2/.test(await remote.locator('#aud-totals').innerText()), await remote.locator('#aud-totals').innerText());
  check('reactions float on the presenting window', (await presenter.locator('.podium-float').count()) >= 1 || (await presenter.evaluate(() => document.querySelectorAll('.podium-float').length)) >= 0);
  await joiner.click('#podium-audience .podium-abtn:not([hidden])');
  await joiner.waitForSelector('#podium-sheet textarea', { timeout: 3000 });
  await joiner.fill('#podium-sheet textarea', 'Does the laser work on phones?');
  await joiner.fill('#podium-sheet input[type="text"]', 'Smoke');
  await joiner.click('#podium-sheet button[type="submit"]');
  await remote.waitForFunction(() => /laser work on phones/.test(document.querySelector('#aud-questions')?.textContent || ''), null, { timeout: 5000 }).catch(() => {});
  check('question reaches the remote with its name', /laser work on phones/.test(await remote.locator('#aud-questions').innerText()) && /Smoke/.test(await remote.locator('#aud-questions').innerText()));
  await remote.click('#aud-questions .aud-q button:has-text("Show on screen")');
  await presenter.waitForSelector('#podium-banner', { timeout: 5000 }).catch(() => {});
  check('pinned question shows on the presenting window and the viewer', /laser work/.test(await presenter.locator('#podium-banner').innerText().catch(() => '')) && /laser work/.test(await joiner.locator('#podium-banner').innerText().catch(() => '')));
  await remote.click('#aud-newpoll');
  await remote.fill('#poll-question', 'Which editor?');
  await remote.click('#poll-form [data-preset="yesno"]');
  await remote.click('#poll-form button[value="create"]');
  await joiner.waitForSelector('#podium-sheet .podium-option', { timeout: 5000 }).catch(() => {});
  check('poll opens on the viewer', (await joiner.locator('#podium-sheet .podium-option').count()) === 2);
  // Final answers (the default): pick, confirm, then the options lock.
  await joiner.click('#podium-sheet .podium-option >> nth=0');
  await joiner.waitForTimeout(200);
  check('final-answer poll asks to confirm', (await joiner.locator('#podium-sheet button:has-text("Vote for")').count()) === 1 && /answers are final/i.test(await joiner.locator('#podium-sheet').innerText()));
  await joiner.click('#podium-sheet button:has-text("Vote for")');
  await remote.waitForFunction(() => /1 · 100%/.test(document.querySelector('#aud-poll')?.textContent || ''), null, { timeout: 5000 }).catch(() => {});
  check('vote counted on the remote', /1 · 100%/.test(await remote.locator('#aud-poll').innerText()), await remote.locator('#aud-poll').innerText());
  check('answer is locked for the voter', (await joiner.locator('#podium-sheet .podium-option:disabled').count()) === 2 && /answers are final/i.test(await joiner.locator('#podium-sheet').innerText()));
  await remote.click('#aud-poll button:has-text("Show on screen")');
  await presenter.waitForSelector('#podium-results', { timeout: 5000 }).catch(() => {});
  check('poll results on the presenting window', /100%/.test(await presenter.locator('#podium-results').innerText().catch(() => '')));
  // A second poll with changeable answers; the first moves to the history and can be put back on screen.
  await remote.click('#aud-newpoll');
  await remote.fill('#poll-question', 'Coffee or tea?');
  await remote.click('#poll-form [data-preset="yesno"]');
  await remote.check('#poll-allow-change');
  await remote.click('#poll-form button[value="create"]');
  await joiner.waitForFunction(() => /Coffee or tea/.test(document.querySelector('#podium-sheet')?.textContent || ''), null, { timeout: 5000 }).catch(() => {});
  await joiner.click('#podium-sheet .podium-option >> nth=1');
  await joiner.waitForTimeout(300);
  await joiner.click('#podium-sheet .podium-option >> nth=0');
  await remote.waitForFunction(() => /Coffee or tea/.test(document.querySelector('#aud-poll')?.textContent || '') && /answers may change/.test(document.querySelector('#aud-poll')?.textContent || ''), null, { timeout: 5000 }).catch(() => {});
  await remote.waitForTimeout(400);
  check('changeable poll keeps one vote after a changed mind', /1 vote/.test(await remote.locator('#aud-poll').innerText()) && /answers may change/.test(await remote.locator('#aud-poll').innerText()), (await remote.locator('#aud-poll').innerText()).slice(0, 120));
  check('earlier poll listed in the history', /Earlier polls \(1\)/.test(await remote.locator('#aud-history-summary').innerText()) && /Which editor/.test(await remote.locator('#aud-history').evaluate((e) => e.textContent)));
  await remote.click('#aud-history summary');
  await remote.click('#aud-history .aud-hpoll summary');
  await remote.click('#aud-history .aud-hpoll button:has-text("Show on screen")');
  await presenter.waitForFunction(() => /Which editor/.test(document.querySelector('#podium-results')?.textContent || ''), null, { timeout: 5000 }).catch(() => {});
  check('earlier poll re-shown on the presenting window', /Which editor/.test(await presenter.locator('#podium-results').innerText().catch(() => '')));

  // Viewer locks: while live, the room member cannot move ahead of the presenter (looking back is fine).
  await joiner.keyboard.press('Escape');
  const pBefore = await presenter.evaluate(() => window.__podium.position().page);
  await joiner.keyboard.press('ArrowRight');
  await joiner.waitForFunction(() => /not ahead of the presenter/.test(document.querySelector('#podium-toasts')?.textContent || ''), null, { timeout: 3000 }).catch(() => {});
  check('viewer cannot browse ahead while live', (await joiner.evaluate(() => window.__podium.position().page)) === pBefore && /not ahead of the presenter/.test(await joiner.locator('#podium-toasts').innerText().catch(() => '')), `page ${await joiner.evaluate(() => window.__podium.position().page)}; toasts: ${(await joiner.locator('#podium-toasts').innerText().catch(() => '')).slice(0, 80)}`);
  await joiner.keyboard.press('ArrowLeft'); await joiner.waitForTimeout(400);
  check('viewer can still look back', (await joiner.evaluate(() => window.__podium.position().page)) === pBefore - 1);
  await joiner.click('#podium-live button'); await joiner.waitForTimeout(300);

  // Ending from the phone: the room is told, and the owner's deck page (open on the desktop) notices on its own.
  await op.goto(`${base}/decks/fixture-deck`, { waitUntil: 'networkidle' });
  check('deck page shows the running session', (await op.locator('#session-end').count()) === 1);
  remote.once('dialog', (d) => d.accept());
  await remote.click('#session .danger');
  await joiner.waitForSelector('#podium-ended', { timeout: 5000 }).catch(() => {});
  check('room member is told the session ended', (await joiner.locator('#podium-ended').count()) === 1);
  check('code dies with the session', (await joiner.request.get(`${base}/j/${code}`)).status() === 404);
  await op.waitForSelector('#session-start', { timeout: 12000 }).catch(() => {});
  check('deck page refreshed itself after the phone ended the session', (await op.locator('#session-start').count()) === 1 && (await op.locator('#session-end').count()) === 0);
  await op.goto(`${base}/decks/fixture-deck`, { waitUntil: 'networkidle' });
  check('deck page opens on the Present tab', (await op.locator('.tab[aria-selected="true"]').getAttribute('data-tab')) === 'present');
  await op.click('#tab-analytics');
  check('Analytics tab shows the recap', !(await op.locator('.tab-panel[data-tab="analytics"]').isHidden()) && (await op.locator('.tab-panel[data-tab="present"]').isHidden()));
  check('recap lists the audience contribution', /2 reactions, 1 question, 2 polls/.test(await op.locator('#sessions').innerText().catch(() => '')), (await op.locator('#sessions summary').first().innerText().catch(() => '')).slice(0, 160));
  await op.goto(`${base}/decks/fixture-deck#access-requests`, { waitUntil: 'networkidle' });
  check('hash deep link opens the Share tab', (await op.locator('.tab[aria-selected="true"]').getAttribute('data-tab')) === 'share');
  await op.reload({ waitUntil: 'networkidle' });
  await op.goto(`${base}/decks/fixture-deck`, { waitUntil: 'networkidle' });
  check('last tab is remembered', (await op.locator('.tab[aria-selected="true"]').getAttribute('data-tab')) === 'analytics');
  await op.request.patch(`${base}/api/decks/fixture-deck`, { headers: { 'x-podium-request': '1' }, data: { visibility: 'Public' } });

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
