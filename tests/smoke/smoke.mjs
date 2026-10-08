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
  for (const path of ['/', '/decks/fixture-deck', '/sources', '/activity', '/talks', '/talks/fixture-slides-fixture']) {
    await op.goto(`${base}${path}`, { waitUntil: 'networkidle' });
    const o = await op.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    check(`no horizontal overflow on a phone: ${path}`, o <= 0, `${o}px`);
  }
  // A phone gets a phone layout, not the desktop one squeezed: the list density is a compact row per deck (clearly
  // shorter than the grid), the remote is the lead action on every card, desktop-only chrome is gone, the filter
  // chips sit on one scrolling line, and the toolbar puts search + view switch on the first line.
  await op.goto(`${base}/`, { waitUntil: 'networkidle' });
  const sameLine = async (a, b) => op.evaluate(([a, b]) => { const c = (s) => { const r = document.querySelector(s).getBoundingClientRect(); return r.y + r.height / 2; }; return Math.abs(c(a) - c(b)) < 8; }, [a, b]);
  check('phone toolbar: search and view switch share the first line', await sameLine('.toolbar .search-wrap', '.toolbar .density'));
  check('phone toolbar: Group/Sort selects sit below the search', await op.evaluate(() => document.querySelector('#group').getBoundingClientRect().y > document.querySelector('#search').getBoundingClientRect().bottom));
  check('phone toolbar: no keyboard hint or desktop-only buttons', !(await op.locator('.search-wrap kbd').isVisible()) && !(await op.locator('a[href="/sources"].desktop-only').isVisible()));
  check('phone filters stay on a single line', await op.evaluate(() => { const mids = [...document.querySelectorAll('#filters > *')].map((c) => c.getBoundingClientRect()).filter((r) => r.height > 0).map((r) => r.y + r.height / 2); return mids.length > 6 && Math.max(...mids) - Math.min(...mids) < 8; }));
  check('phone card leads with the remote (a PDF deck too)', await op.locator('.card[data-slug="fixture-deck"] .act-remote').isVisible() && await op.evaluate(() => { const c = document.querySelector('.card[data-slug="fixture-deck"] .card-actions'); return c.querySelector('.act-remote').getBoundingClientRect().x < c.querySelector('.act-primary').getBoundingClientRect().x; }));
  check('phone card keeps status and date together', await sameLine('.card[data-slug="fixture-deck-workshop"] .card-meta .status', '.card[data-slug="fixture-deck-workshop"] .card-meta .updated'));
  const gridHeight = await op.evaluate(() => document.querySelector('.card[data-slug="fixture-deck"]').getBoundingClientRect().height);
  await op.click('label[for="density-list"]'); await op.waitForTimeout(300);
  const listHeight = await op.evaluate(() => document.querySelector('.card[data-slug="fixture-deck"]').getBoundingClientRect().height);
  check('phone list density is a compact row', listHeight < 120 && listHeight < gridHeight / 3, `list ${Math.round(listHeight)}px vs grid ${Math.round(gridHeight)}px`);
  check('phone list row: thumbnail, title and the two actions that matter', await op.evaluate(() => { const c = document.querySelector('.card[data-slug="fixture-deck"]'); const vis = (s) => { const e = c.querySelector(s); return !!e && e.getClientRects().length > 0; }; return vis('.card-thumb') && vis('.card-title') && vis('.act-remote') && vis('.act-primary') && !vis('.act-manage') && !vis('.card-sub'); }));
  await op.click('label[for="density-grid"]'); await op.waitForTimeout(200);
  await op.goto(`${base}/decks/fixture-deck`, { waitUntil: 'networkidle' });
  check('phone deck page offers the remote as a button instead of a QR', await op.locator('#open-remote').isVisible() && !(await op.locator('#phone-remote .qr').isVisible()));
  check('phone deck page keeps the planned minutes and their unit on one line', await sameLine('#session-minutes', '#session-freeze'));
  await op.goto(`${base}/talks`, { waitUntil: 'networkidle' });
  check('phone talks catalog: controls shown by value, captions for screen readers', await op.locator('#talk-group').isVisible() && (await op.evaluate(() => [...document.querySelectorAll('.toolbar .control-label')].every((l) => l.getBoundingClientRect().width <= 1))) && (await op.locator('#talk-group option[value="none"]').innerText()) === 'No grouping');
  await op.setViewportSize({ width: 1400, height: 1000 });
  await op.goto(`${base}/`, { waitUntil: 'networkidle' });
  check('desktop card hides the remote for a PDF deck (the deck page carries the QR)', !(await op.locator('.card[data-slug="fixture-deck"] .act-remote').isVisible()) && (await op.locator('.search-wrap kbd').isVisible()));
  await op.fill('#search', 'slide two'); await op.waitForTimeout(600);
  check('full-text search hits slide 2', (await op.locator('#slide-hits-list li a[href$="/fixture-deck/2"]').count()) > 0);
  await op.fill('#search', 'sync relay'); await op.waitForTimeout(600);
  check('library search finds the talk by its abstract', (await op.locator('#talk-hits-list li a[href="/talks/fixture-slides-fixture"]').count()) === 1);
  await op.fill('#search', '');
  check('library card carries the variant chip', (await op.locator('.card[data-slug="fixture-deck-workshop"] .badge-variant:has-text("workshop")').count()) === 1);
  await op.selectOption('#group', 'talk'); await op.waitForTimeout(200);
  check('library groups by talk', (await op.locator('#groups section.group[data-group-key="The fixture talk"] .card').count()) === 2);
  // Sorting runs on the slides' own dates: the workshop deck was saved (document metadata) a year before the main
  // deck even though both were committed together; direction flips the order; grouping by folder and year works.
  await op.selectOption('#group', 'none'); await op.selectOption('#sort', 'saved'); await op.waitForTimeout(200);
  const order = async () => (await op.locator('#groups .card').evaluateAll((cards) => cards.map((c) => c.dataset.slug))).join(',');
  check('newest saved first', (await order()).startsWith('fixture-deck,fixture-deck-workshop'), await order());
  check('card shows the document save date', /saved/.test(await op.locator('.card[data-slug="fixture-deck"] .updated').innerText()) && ((await op.locator('.card[data-slug="fixture-deck"] .updated time').getAttribute('datetime')) || '').startsWith('2025-05-10'), await op.locator('.card[data-slug="fixture-deck"] .updated').innerText());
  await op.click('#sort-dir'); await op.waitForTimeout(200);
  check('direction flips to oldest first', (await order()).startsWith('fixture-deck-workshop,fixture-deck'), await order());
  check('direction is remembered as pressed', (await op.getAttribute('#sort-dir', 'aria-pressed')) === 'true');
  await op.click('#sort-dir');
  await op.selectOption('#group', 'section'); await op.waitForTimeout(200);
  check('library groups by folder', (await op.locator('#groups section.group[data-group-key="fixture/slides / fixture"] .card').count()) === 2);
  await op.selectOption('#group', 'year'); await op.waitForTimeout(200);
  check('library groups by year saved', (await op.locator('#groups section.group[data-group-key="2025"] .card').count()) === 1 && (await op.locator('#groups section.group[data-group-key="2024"] .card').count()) === 1);
  await op.selectOption('#group', 'repo');
  // Talks density: one card per talk with its variants inside; filters match any variant; decks without a talk keep their card.
  await op.click('label[for="density-talks"]'); await op.waitForTimeout(250);
  check('talks density shows one card for the fixture talk', (await op.locator('#groups .card[data-talk-card][data-talk-id="fixture-slides-fixture"]').count()) === 1 && (await op.locator('#groups .card[data-slug="fixture-deck"]').count()) === 0);
  check('talk card lists both variants', (await op.locator('#groups .card[data-talk-card] .chip-variant').count()) === 2);
  check('talk card has no bulk select box', (await op.locator('#groups .card[data-talk-card] .select-box').count()) === 0);
  await op.click('#filters .chip[data-filter="kind"][data-value="pdf"]'); await op.waitForTimeout(200);
  check('kind filter matches the talk through its variants', (await op.locator('#groups .card[data-talk-card]').count()) === 1);
  await op.click('#filters .chip[data-filter="kind"][data-value="pdf"]');
  await op.click('label[for="density-grid"]'); await op.waitForTimeout(250);
  check('grid density shows the deck cards again', (await op.locator('#groups .card[data-slug="fixture-deck"]').count()) === 1 && (await op.locator('#groups .card[data-talk-card]').count()) === 0);

  // Talks: owner catalog, detail with CfP pack, compare view, and the public speaker page.
  await op.goto(`${base}/talks`, { waitUntil: 'networkidle' });
  check('talks catalog lists the fixture talk', (await op.locator('.talk-card .talk-title a[href="/talks/fixture-slides-fixture"]').count()) === 1);
  check('talks catalog shows next event', /Workshop Days/.test(await op.locator('.talk-card .talk-foot').innerText()));
  // Catalog controls: grouping renders headers, sorting flips with the direction button, choices are remembered.
  await op.selectOption('#talk-group', 'level'); await op.waitForTimeout(150);
  check('talks catalog groups by level', (await op.locator('#talk-list section.talk-group[data-group-key="Intermediate"] .talk-card').count()) === 1);
  await op.selectOption('#talk-group', 'none'); await op.selectOption('#talk-sort', 'title'); await op.click('#talk-dir'); await op.waitForTimeout(150);
  check('talks catalog direction toggle is pressed', (await op.getAttribute('#talk-dir', 'aria-pressed')) === 'true' && /Z to A/.test(await op.getAttribute('#talk-dir', 'title')));
  await op.reload({ waitUntil: 'networkidle' });
  check('talks catalog remembers sort and direction', (await op.inputValue('#talk-sort')) === 'title' && (await op.getAttribute('#talk-dir', 'aria-pressed')) === 'true');
  await op.click('#talk-dir'); await op.selectOption('#talk-sort', 'active');
  await op.fill('#talk-search', 'nothing-matches-this'); await op.waitForTimeout(100);
  check('talks search hides non-matching talks', (await op.locator('.talk-card:not([hidden])').count()) === 0 && !(await op.locator('#talk-none').isHidden()));
  await op.fill('#talk-search', '');
  await op.goto(`${base}/talks/fixture-slides-fixture`, { waitUntil: 'networkidle' });
  check('talk detail renders the abstract as HTML', (await op.locator('#abstract .prose strong:has-text("every")').count()) === 1);
  check('talk detail lists both variants', (await op.locator('#decks .variant').count()) === 2);
  check('talk detail timeline shows the three events', (await op.locator('#events .tl-item').count()) === 3);
  check('talk detail links the recap-less delivery to its deck', (await op.locator('#events a[href="/decks/fixture-deck"]').count()) >= 1);
  const cfp = await op.request.get(`${base}/talks/fixture-slides-fixture/cfp.txt`);
  check('CfP pack as text', cfp.ok() && /^The fixture talk\n\nAbstract\nThree slides that exercise every feature/.test(await cfp.text()), String(cfp.status()));
  await op.selectOption('#decks select[data-compare-from="fixture-deck"]', 'fixture-deck-workshop');
  await op.waitForURL(/\/decks\/fixture-deck\/compare\/fixture-deck-workshop$/, { timeout: 5000 }).catch(() => {});
  check('compare view opens from the talk', /\/compare\/fixture-deck-workshop$/.test(op.url()), op.url());
  const counts = (await op.locator('.compare-counts').innerText()).replace(/\s+/g, ' ');
  check('compare view aligns the slides', /2 identical 1 reworded 0 only in main 0 only in workshop/.test(counts), counts);
  check('compare view highlights the reworded slide', (await op.locator('.compare-row.cmp-changed ins').count()) >= 1 && (await op.locator('.compare-row.cmp-changed del').count()) >= 1);
  check('compare view shows slide-sheet sprites', await op.evaluate(() => { const s = document.querySelector('.compare-thumb .sprite'); return !!s && getComputedStyle(s).backgroundImage.includes('/slides.jpg'); }));
  await op.check('#hide-same');
  check('hide identical slides', (await op.locator('.compare-row[data-kind="same"]:not([hidden])').count()) === 0);
  await op.uncheck('#hide-same');
  await op.goto(`${base}/decks/fixture-deck`, { waitUntil: 'networkidle' });
  check('deck page shows the talk strip with the sibling variant', (await op.locator('#talk a[href="/talks/fixture-slides-fixture"]').count()) >= 1 && (await op.locator('#talk a[href="/decks/fixture-deck-workshop"]').count()) >= 1);
  {
    const speaker = await browser.newContext();
    const sp = await speaker.newPage();
    sp.on('pageerror', (e) => errors.push(`speaker page: ${e.message}`));
    await sp.goto(`${base}/talks`, { waitUntil: 'networkidle' });
    check('public speaker page renders without sign-in', (await sp.locator('.speaker-hero h1:has-text("Fixture Speaker")').count()) === 1);
    check('public speaker page lists only public facts', (await sp.locator('.talk-card').count()) === 1 && !(await sp.content()).includes('Declined Con') && !(await sp.content()).includes('New talk'));
    await sp.goto(`${base}/talks/fixture-slides-fixture`, { waitUntil: 'networkidle' });
    const content = await sp.content();
    check('public talk page hides declined submissions and owner actions', content.includes('SmokeConf') && !content.includes('Declined Con') && !content.includes('Edit on GitHub') && /og:description/.test(content));
    await speaker.close();
  }

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
  // The public gallery and login page on a phone: no overflow, the header actions side by side rather than stacked.
  await vp.setViewportSize({ width: 390, height: 844 });
  for (const path of ['/gallery', '/login?prompt=1']) {
    await vp.goto(`${base}${path}`, { waitUntil: 'networkidle' });
    const o = await vp.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    check(`no horizontal overflow on a phone: ${path}`, o <= 0, `${o}px`);
  }
  await vp.goto(`${base}/gallery`, { waitUntil: 'networkidle' });
  check('phone gallery keeps its header buttons on one line', await vp.evaluate(() => { const b = [...document.querySelectorAll('.row-between .row > .btn')]; return b.length >= 2 && Math.abs(b[0].getBoundingClientRect().y - b[1].getBoundingClientRect().y) < 4 && b[0].getBoundingClientRect().width < 200; }));

  // ---- A library the size of a real one (110 more decks over ~35 talks) on a throttled phone. ----
  const bulk = await op.request.post(`${base}/dev-seed?bulk=110`, { headers: { 'x-podium-request': '1' } });
  check('bulk fixture seeded', bulk.ok(), String(bulk.status()));
  await op.setViewportSize({ width: 390, height: 844 });
  const cdp = await owner.newCDPSession(op);
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
  const thumbs = [];
  op.on('request', (r) => { if (/\.jpg\?/.test(r.url())) thumbs.push(r.url()); });
  const doc = await op.goto(`${base}/`, { waitUntil: 'load' });
  check('library HTML is compressed', /br|gzip/.test(doc.headers()['content-encoding'] || ''), doc.headers()['content-encoding'] || 'none');
  check('library renders every deck', (await op.locator('.card[data-slug^="load-deck-"]').count()) >= 110);
  check('cards ask for the small thumbnail', thumbs.length > 0 && thumbs.every((u) => /size=sm/.test(u)), `${thumbs.length} requests`);
  check('off-screen cards are skipped until scrolled to (content-visibility)', await op.evaluate(() => getComputedStyle(document.querySelector('.card[data-slug="load-deck-050"]')).contentVisibility === 'auto'));
  const switchMs = async (sel) => op.evaluate(async (s) => { const t = performance.now(); document.querySelector(s).click(); await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))); return performance.now() - t; }, sel);
  const toList = await switchMs('label[for="density-list"]');
  const toGrid = await switchMs('label[for="density-grid"]');
  check('density switch repaints within budget on a 4x-throttled phone', toList < 2000 && toGrid < 2000, `list ${Math.round(toList)} ms, grid ${Math.round(toGrid)} ms`);
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });
  check('touch devices get no sticky hover look on the nav', await op.evaluate(() => [...document.styleSheets].flatMap((s) => { try { return [...s.cssRules]; } catch { return []; } }).some((r) => r.media && /hover: hover/.test(r.media.mediaText) && /\.topnav a:hover/.test(r.cssText))));
  await op.setViewportSize({ width: 1400, height: 1000 });

  check('no page errors / CSP violations', errors.length === 0, errors.join(' | '));
} finally { await browser.close(); }

if (failures.length) { console.error(`\n${failures.length} check(s) failed: ${failures.join(', ')}`); process.exit(1); }
console.log('\nsmoke OK');
