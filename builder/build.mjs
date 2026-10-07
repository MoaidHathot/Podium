#!/usr/bin/env node
// Podium builder: runs inside an ephemeral container (or locally in development) and turns one deck at one commit
// into static artifacts, then uploads them and reports back. Configuration comes exclusively from PODIUM_* env vars
// (see BuilderEnvironment in Podium.Web). All secrets are removed from process.env before any deck code can run.

import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync, cpSync, readdirSync, lstatSync, copyFileSync } from 'node:fs';
import { join, resolve, extname, relative, dirname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { normalizeAnnotation } from './lib/annotations.mjs';
import { which } from './lib/shared.mjs';
import { buildSlidev } from './kinds/slidev.mjs';
import { buildPresenterm } from './kinds/presenterm.mjs';
import { buildStatic } from './kinds/static.mjs';
import { buildPowerPoint, buildPdfDeck } from './kinds/files.mjs';

const require = createRequire(import.meta.url);
const here = dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------------------------------------------
// Inputs
// In the container, entrypoint.sh copies the PODIUM_* variables into a root-only file and re-executes node with an
// empty environment, so nothing sensitive is left in /proc/*/environ. The file is deleted as soon as it is read.
// In development (LocalProcess runner) the variables arrive directly in the environment.
// ---------------------------------------------------------------------------------------------------------------
const env = {};
if (process.env.PODIUM_CONFIG_FILE) {
  Object.assign(env, JSON.parse(readFileSync(process.env.PODIUM_CONFIG_FILE, 'utf8')));
  rmSync(process.env.PODIUM_CONFIG_FILE, { force: true });
}
for (const [k, v] of Object.entries(process.env)) {
  if (k.startsWith('PODIUM_')) { env[k] = v; delete process.env[k]; }
}
// A production NODE_ENV makes npm skip devDependencies and breaks Slidev's exporter; builds always run in dev mode.
delete process.env.NODE_ENV;
const need = (k) => { if (!env[k]) throw new Error(`Missing ${k}`); return env[k]; };

// Deck-controlled steps run as this unprivileged user when the orchestrator itself is root (container). Deck code then
// cannot read the orchestrator's files or memory, where the clone token, upload SAS and callback token live.
const deckUser = env.PODIUM_DECK_USER || null;
const dropPrivileges = process.platform !== 'win32' && typeof process.getuid === 'function' && process.getuid() === 0 && !!deckUser;

const buildId = need('PODIUM_BUILD_ID');
const slug = need('PODIUM_DECK_SLUG');
const kind = need('PODIUM_DECK_KIND'); // slidev | presenterm | static
const deckPath = (env.PODIUM_DECK_PATH || '').replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
const entry = need('PODIUM_DECK_ENTRY');
const basePath = env.PODIUM_BASE_PATH || `/d/${slug}/`;
const cloneUrl = need('PODIUM_CLONE_URL');
const sha = need('PODIUM_SHA');
const uploadUrl = need('PODIUM_UPLOAD_URL');
const callbackUrl = need('PODIUM_CALLBACK_URL');
const callbackToken = need('PODIUM_CALLBACK_TOKEN');
const timeoutSec = Number(env.PODIUM_TIMEOUT_SEC || 900);
const exportPdf = env.PODIUM_EXPORT_PDF === '1';
const exportPptx = env.PODIUM_EXPORT_PPTX === '1';
const trusted = env.PODIUM_TRUSTED === '1';
const stripNotes = env.PODIUM_STRIP_NOTES === '1';
const workRoot = env.PODIUM_WORKDIR || '/work';

// Anything that must never appear in logs.
const secrets = [callbackToken];
try {
  const u = new URL(cloneUrl);
  if (u.password) secrets.push(u.password);
  if (u.username && u.username !== 'x-access-token') secrets.push(u.username);
} catch { /* not a URL */ }
try { const q = new URL(uploadUrl).search; if (q.length > 1) secrets.push(q.slice(1)); } catch { /* file path */ }

// ---------------------------------------------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------------------------------------------
// The build log is uploaded with the artifacts and shown in the UI. Deck tooling can be very chatty (npm, Vite,
// LibreOffice); keep the head (what was attempted) and the tail (what went wrong) and drop the middle.
const LOG_MAX_LINES = Number(env.PODIUM_LOG_MAX_LINES) || 4000;
const LOG_MAX_LINE_CHARS = 2000;
const logLines = [];
let droppedLines = 0;
const scrub = (s) => secrets.reduce((acc, sec) => (sec ? acc.split(sec).join('***') : acc), String(s));
function log(msg) {
  let text = scrub(msg);
  if (text.length > LOG_MAX_LINE_CHARS) text = `${text.slice(0, LOG_MAX_LINE_CHARS)} ... [${text.length - LOG_MAX_LINE_CHARS} chars trimmed]`;
  const line = `[${new Date().toISOString()}] ${text}`;
  if (logLines.length >= LOG_MAX_LINES) { logLines.splice(Math.floor(LOG_MAX_LINES / 4), 1); droppedLines++; }
  logLines.push(line);
  process.stdout.write(line + '\n');
}
function logText() {
  if (!droppedLines) return logLines.join('\n') + '\n';
  const head = Math.floor(LOG_MAX_LINES / 4);
  return [...logLines.slice(0, head), `... ${droppedLines} line(s) trimmed from the middle of the log ...`, ...logLines.slice(head)].join('\n') + '\n';
}

// ---------------------------------------------------------------------------------------------------------------
// Process helpers
// ---------------------------------------------------------------------------------------------------------------
const deadline = Date.now() + Math.max(60, timeoutSec - 30) * 1000;
const remainingMs = () => Math.max(1000, deadline - Date.now());

/** Environment handed to deck-controlled child processes: a curated allow-list, never the orchestrator's own. */
function childEnv(extra) {
  const base = {
    PATH: process.env.PATH, HOME: dropPrivileges ? `/home/${deckUser}` : process.env.HOME, LANG: process.env.LANG || 'C.UTF-8',
    TMPDIR: process.env.TMPDIR, TEMP: process.env.TEMP, TMP: process.env.TMP,
    PLAYWRIGHT_BROWSERS_PATH: process.env.PLAYWRIGHT_BROWSERS_PATH,
    NPM_CONFIG_UPDATE_NOTIFIER: 'false', NPM_CONFIG_FUND: 'false', NPM_CONFIG_AUDIT: 'false',
    CI: '1', FORCE_COLOR: '0', NO_COLOR: '1', GIT_TERMINAL_PROMPT: '0',
  };
  // Windows (development only, no isolation goal): tools need the whole machine environment.
  const merged = process.platform === 'win32' ? { ...process.env, ...base, ...extra } : { ...base, ...extra };
  for (const k of Object.keys(merged)) if (merged[k] === undefined || merged[k] === null) delete merged[k];
  return merged;
}

/** Runs a deck-controlled command; as root it is re-launched under the unprivileged deck user. */
function run(cmd, args, { cwd, envExtra = {}, allowFail = false, timeoutMs, echo = true, privileged = false } = {}) {
  return new Promise((resolvePromise, reject) => {
    const display = `${cmd} ${args.map((a) => (a.includes(' ') ? JSON.stringify(a) : a)).join(' ')}`;
    if (echo) log(`$ ${display}`);
    let file = cmd, argv = args;
    if (dropPrivileges && !privileged) {
      file = 'setpriv';
      argv = ['--reuid', deckUser, '--regid', deckUser, '--init-groups', '--', cmd, ...args];
    }
    const child = spawn(file, argv, {
      cwd,
      env: childEnv(envExtra),
      stdio: ['ignore', 'pipe', 'pipe'],
      shell: process.platform === 'win32' && /\.(cmd|bat)$/i.test(cmd),
      // Own process group on POSIX so a timeout can take the whole tree down (git -> git-remote-https, npm -> node).
      detached: process.platform !== 'win32',
    });
    let out = '';
    const onData = (d) => { const s = d.toString(); out += s; for (const l of s.split(/\r?\n/)) if (l.trim()) log(`  ${l}`); };
    child.stdout.on('data', onData);
    child.stderr.on('data', onData);
    let settled = false;
    const settle = (code) => {
      if (settled) return;
      settled = true;
      clearTimeout(t);
      if (code === 0 || allowFail) resolvePromise({ code, out });
      else reject(new Error(`${cmd} exited with code ${code}`));
    };
    const t = setTimeout(() => { log(`Timeout after ${Math.round((timeoutMs ?? remainingMs()) / 1000)}s, killing ${cmd}`); killTree(child); }, timeoutMs ?? remainingMs());
    child.on('error', (e) => { if (!settled) { settled = true; clearTimeout(t); reject(e); } });
    // 'close' waits for the stdio pipes, which grandchildren that outlived a kill may still hold; 'exit' is the
    // authoritative end of the command, so settle shortly after it if the pipes have not drained by then.
    child.on('close', (code) => settle(code));
    child.on('exit', (code, signal) => setTimeout(() => settle(code ?? (signal ? 137 : 1)), 1000));
  });
}

function killTree(child) {
  try {
    if (process.platform === 'win32') spawnSync('taskkill', ['/T', '/F', '/PID', String(child.pid)], { stdio: 'ignore' });
    else process.kill(-child.pid, 'SIGKILL');
  } catch { try { child.kill('SIGKILL'); } catch { } }
}

const npmCmd = process.platform === 'win32' ? 'npm.cmd' : 'npm';
const npxCmd = process.platform === 'win32' ? 'npx.cmd' : 'npx';

/** Creates a directory that deck-controlled steps will write to (owned by the deck user when privileges are dropped). */
function mkdirForDeck(dir) {
  mkdirSync(dir, { recursive: true });
  if (dropPrivileges) spawnSync('chown', ['-R', `${deckUser}:${deckUser}`, dir], { stdio: 'ignore' });
}

/** Hands a file the orchestrator wrote to the deck user. */
function ownForDeck(path) {
  if (dropPrivileges) spawnSync('chown', [`${deckUser}:${deckUser}`, path], { stdio: 'ignore' });
}

/** Copies a tree so that the deck user owns the copy (Vite writes caches into node_modules). */
async function copyTreeForDeck(from, to) {
  // Running as the deck user, `cp` creates files owned by that user; --no-preserve stops it from carrying over the
  // source's root ownership/mode, which would leave node_modules read-only for the build (EACCES on .slidev/virtual).
  if (dropPrivileges) await run('cp', ['-rL', '--no-preserve=ownership,mode', from, to], { echo: false });
  else cpSync(from, to, { recursive: true, dereference: true });
}

// ---------------------------------------------------------------------------------------------------------------
// Steps
// ---------------------------------------------------------------------------------------------------------------
async function checkout(repoDir) {
  mkdirForDeck(repoDir);
  const u = new URL(cloneUrl);
  const authHeader = u.username || u.password ? `Authorization: Basic ${Buffer.from(`${decodeURIComponent(u.username)}:${decodeURIComponent(u.password)}`).toString('base64')}` : null;
  u.username = ''; u.password = '';
  const cleanUrl = u.toString();
  const gitAuth = authHeader ? ['-c', `http.extraHeader=${authHeader}`] : [];
  secrets.push(...(authHeader ? [authHeader.slice('Authorization: Basic '.length)] : []));

  // Fetch exactly the requested commit; the credential lives only on this command line, never in .git/config.
  // A transfer that stalls below 1 KB/s for 45 s is aborted and retried once rather than hanging until the build timeout.
  // Decks in a sub-folder use a partial clone (no blobs) plus a sparse checkout, so the download is the deck rather
  // than every file in the repository: a cone of the deck folder for Slidev/presenterm/static decks (they may import
  // siblings), just the file itself (plus a same-named PDF export) for PowerPoint/PDF decks, whose folder may hold
  // dozens of other presentations. The lazy blob fetch during checkout reuses the same one-shot credential.
  const fileDeck = kind === 'powerpoint' || kind === 'pdf';
  const sparse = deckPath.length > 0 || fileDeck;
  const gitTransfer = [...gitAuth, '-c', 'http.lowSpeedLimit=1000', '-c', 'http.lowSpeedTime=45', '-c', 'credential.helper='];
  const fetchArgs = [...gitTransfer, 'fetch', '-q', '--depth', '1', ...(sparse ? ['--filter=blob:none'] : []), 'origin', sha];
  const filePrefix = deckPath ? `/${deckPath}/` : '/';
  const sidecarPdf = `${filePrefix}${entry.replace(/\.[^.]+$/, '')}.pdf`;
  const sparseArgs = fileDeck
    ? ['sparse-checkout', 'set', '--no-cone', `${filePrefix}${entry}`, ...(sidecarPdf !== `${filePrefix}${entry}` ? [sidecarPdf] : [])]
    : ['sparse-checkout', 'set', '--cone', deckPath];
  for (let attempt = 1; ; attempt++) {
    // A killed fetch leaves lock files behind (shallow.lock, FETCH_HEAD.lock): every attempt starts from a fresh repo.
    // GitHub hiccups tend to last a minute or two, so later attempts wait before trying again.
    if (attempt > 1) { await new Promise((r) => setTimeout(r, Math.min(remainingMs() / 4, attempt === 2 ? 20_000 : 60_000))); rmSync(repoDir, { recursive: true, force: true }); mkdirForDeck(repoDir); }
    await run('git', ['init', '-q'], { cwd: repoDir, echo: attempt === 1 });
    await run('git', ['remote', 'add', 'origin', cleanUrl], { cwd: repoDir, echo: attempt === 1 });
    if (sparse) await run('git', sparseArgs, { cwd: repoDir, echo: attempt === 1 });
    log(`$ git fetch --depth 1${sparse ? ' --filter=blob:none' : ''} origin ${sha.slice(0, 7)}${attempt > 1 ? ` (attempt ${attempt})` : ''}`);
    const r = await run('git', fetchArgs, { cwd: repoDir, echo: false, allowFail: true, timeoutMs: Math.min(remainingMs(), 4 * 60 * 1000), envExtra: { GCM_INTERACTIVE: 'never' } });
    if (r.code !== 0) { if (attempt >= 3) throw new Error(`git fetch failed (exit ${r.code})`); continue; }
    const c = await run('git', [...gitTransfer, 'checkout', '-q', 'FETCH_HEAD'], { cwd: repoDir, echo: false, allowFail: true, timeoutMs: Math.min(remainingMs(), 4 * 60 * 1000), envExtra: { GCM_INTERACTIVE: 'never' } });
    if (c.code === 0) break;
    if (attempt >= 3) throw new Error(`git checkout failed (exit ${c.code})`);
  }
  // Deck code runs during the build; make sure nothing sensitive is on disk when it does.
  rmSync(join(repoDir, '.git'), { recursive: true, force: true });
}

const result_annotations = [];
function addAnnotation(path, line, level, message) {
  const a = normalizeAnnotation({ path, line, level, message });
  if (a && result_annotations.length < 50) result_annotations.push(a);
}

// Everything a deck-kind module (kinds/*.mjs) needs from the orchestrator. Kind modules never touch module state here.
const SHEET_COLS = 6;
const SHEET_MAX_PAGES = 400;
const ctx = {
  env, buildId, slug, kind, deckPath, entry, basePath, workRoot, here, require,
  exportPdf, exportPptx, trusted, stripNotes, npmCmd, npxCmd, SHEET_MAX_PAGES,
  log, run, remainingMs, mkdirForDeck, ownForDeck, copyTreeForDeck, addAnnotation,
};

// ---------------------------------------------------------------------------------------------------------------
// Thumbnails
// ---------------------------------------------------------------------------------------------------------------
async function makeThumbnail(outDir, result) {
  const target = join(outDir, 'thumbnail.jpg');
  try {
    if (kind === 'powerpoint' || kind === 'pdf') {
      if (!existsSync(join(outDir, 'deck.pdf'))) return; // nothing worth previewing (download-only fallback page)
      const pdftoppm = which('pdftoppm');
      if (!pdftoppm) { log('pdftoppm not available; no thumbnail'); return; }
      const prefix = join(workRoot, 'thumb');
      const r = await run(pdftoppm, ['-jpeg', '-jpegopt', 'quality=80', '-f', '1', '-l', '1', '-scale-to-x', '1280', '-scale-to-y', '-1', join(outDir, 'deck.pdf'), prefix], { allowFail: true, timeoutMs: 60000 });
      const produced = readdirSync(workRoot).find((f) => f.startsWith('thumb') && f.endsWith('.jpg'));
      if (r.code === 0 && produced) { copyFileSync(join(workRoot, produced), target); result.hasThumbnail = true; }
      return;
    }
    // Chromium must not run as root (and must not run with the orchestrator's privileges at all): delegate to a
    // child script that executes as the deck user.
    const r = await run(process.execPath, [join(here, 'thumb.mjs'), join(outDir, 'site'), basePath, kind, target], { allowFail: true, timeoutMs: Math.min(remainingMs(), 90000), envExtra: { NODE_PATH: join(here, 'node_modules') } });
    result.hasThumbnail = r.code === 0 && existsSync(target);
  } catch (e) {
    log(`Thumbnail skipped: ${e.message}`);
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Slide sheet (all pages tiled into one JPEG), per-page text for search, manifest for offline precaching
// ---------------------------------------------------------------------------------------------------------------

async function makeSlideSheetAndText(outDir, result) {
  const pdf = join(outDir, 'deck.pdf');
  const slidevSlides = kind === 'slidev' && result.hasNotes ? result.slideCount : 0; // slide count from the parser

  // --- Text for search. Slidev: already written by notes.mjs in slide numbering (its PDF may have a page per click
  // step). Everything else: one record per PDF page.
  if (kind === 'slidev') {
    result.hasText = existsSync(join(outDir, 'text.json'));
  } else if (existsSync(pdf)) {
    const pdftotext = which('pdftotext');
    const pdfinfo = which('pdfinfo');
    if (pdfinfo) {
      const info = await run(pdfinfo, [pdf], { allowFail: true, echo: false, timeoutMs: 30000 });
      const m = /Pages:\s+(\d+)/.exec(info.out || '');
      if (m) result.slideCount = Number(m[1]);
    }
    if (pdftotext) {
      const r = await run(pdftotext, ['-layout', '-enc', 'UTF-8', pdf, '-'], { allowFail: true, echo: false, timeoutMs: Math.min(remainingMs(), 60000) });
      if (r.code === 0) {
        const chunks = String(r.out).split('\f');
        if (chunks.length && !chunks[chunks.length - 1].trim()) chunks.pop();
        const text = chunks.slice(0, SHEET_MAX_PAGES).map((t, i) => ({ index: i + 1, title: null, text: t.replace(/[ \t]+/g, ' ').replace(/\n{2,}/g, '\n').trim().slice(0, 20000) }));
        writeFileSync(join(outDir, 'text.json'), JSON.stringify(text));
        result.hasText = true;
        if (!result.slideCount) result.slideCount = text.length;
      } else result.warnings.push('Slide text could not be extracted (pdftotext failed)');
    }
  }

  // --- Slide sheet. Slidev: screenshot every slide of the built site (true slide numbering, no PDF needed).
  // Others: tile the PDF pages.
  const out = join(outDir, 'slides.jpg');
  let geometry = null;
  if (kind === 'slidev' && slidevSlides > 0 && slidevSlides <= SHEET_MAX_PAGES) {
    const r = await run(process.execPath, [join(here, 'sheet.mjs'), '--site', join(outDir, 'site'), basePath, String(slidevSlides), String(SHEET_COLS), out], { allowFail: true, timeoutMs: Math.min(remainingMs(), 4 * 60 * 1000), envExtra: { NODE_PATH: join(here, 'node_modules') } });
    geometry = r.code === 0 ? lastJsonLine(r.out) : null;
  } else if (kind !== 'slidev' && existsSync(pdf) && result.slideCount > 0 && result.slideCount <= SHEET_MAX_PAGES) {
    const pdftoppm = which('pdftoppm');
    if (pdftoppm) {
      const dir = join(workRoot, 'sheet');
      rmSync(dir, { recursive: true, force: true });
      mkdirForDeck(dir);
      const p = await run(pdftoppm, ['-jpeg', '-jpegopt', 'quality=70', '-scale-to-x', '320', '-scale-to-y', '-1', pdf, join(dir, 'p')], { allowFail: true, echo: false, timeoutMs: Math.min(remainingMs(), 120000) });
      if (p.code === 0) {
        const r = await run(process.execPath, [join(here, 'sheet.mjs'), '--tiles', dir, String(SHEET_COLS), out], { allowFail: true, timeoutMs: Math.min(remainingMs(), 120000), envExtra: { NODE_PATH: join(here, 'node_modules') } });
        geometry = r.code === 0 ? lastJsonLine(r.out) : null;
      }
    }
  }
  if (geometry && existsSync(out)) {
    writeFileSync(join(outDir, 'slides.json'), JSON.stringify(geometry));
    result.hasSlideSheet = true;
  } else if (result.slideCount > 0) {
    rmSync(out, { force: true });
    result.warnings.push('Slide sheet could not be composed (see build log)');
  }
}

function lastJsonLine(out) {
  const lines = String(out || '').trim().split(/\r?\n/).reverse();
  for (const l of lines) { const s = l.replace(/^\[[^\]]*\]\s*/, '').trim(); if (s.startsWith('{')) { try { return JSON.parse(s); } catch { /* keep looking */ } } }
  return null;
}
/** Lists every site file per variant so a service worker can precache a build; never includes presenter-only data. */
function writeManifest(outDir, result) {
  const manifest = { build: buildId, variants: {} };
  for (const variant of ['site', 'site-public']) {
    const dir = join(outDir, variant);
    if (!existsSync(dir)) continue;
    const files = [...walk(dir)].map((f) => f.rel).filter((rel) => !rel.endsWith('.map')).sort();
    manifest.variants[variant] = files.slice(0, 5000);
  }
  if (Object.keys(manifest.variants).length) writeFileSync(join(outDir, 'manifest.json'), JSON.stringify(manifest));
}

// ---------------------------------------------------------------------------------------------------------------
// Upload
// ---------------------------------------------------------------------------------------------------------------
const contentTypes = {
  '.html': 'text/html; charset=utf-8', '.htm': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8', '.map': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png',
  '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.gif': 'image/gif', '.webp': 'image/webp', '.avif': 'image/avif', '.ico': 'image/x-icon', '.woff': 'font/woff',
  '.woff2': 'font/woff2', '.ttf': 'font/ttf', '.otf': 'font/otf', '.mp4': 'video/mp4', '.webm': 'video/webm', '.mp3': 'audio/mpeg', '.wav': 'audio/wav',
  '.pdf': 'application/pdf', '.pptx': 'application/vnd.openxmlformats-officedocument.presentationml.presentation', '.txt': 'text/plain; charset=utf-8',
  '.md': 'text/markdown; charset=utf-8', '.wasm': 'application/wasm', '.webmanifest': 'application/manifest+json', '.xml': 'application/xml', '.log': 'text/plain; charset=utf-8',
};

function* walk(dir, base = dir) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    // Deck code wrote this tree: never follow symlinks (loops, or links pointing outside the output).
    const st = lstatSync(p);
    if (st.isSymbolicLink()) continue;
    if (st.isDirectory()) yield* walk(p, base);
    else if (st.isFile()) yield { path: p, rel: relative(base, p).split(sep).join('/'), size: st.size };
  }
}

// Output budget: a deck cannot fill the storage account. Over budget, only the build log is uploaded and the build fails.
const maxOutputBytes = (Number(env.PODIUM_MAX_OUTPUT_MB) || (trusted ? 1024 : 256)) * 1024 * 1024;
const maxOutputFiles = Number(env.PODIUM_MAX_OUTPUT_FILES) || 20000;

function enforceOutputBudget(outDir, result) {
  const files = [...walk(outDir)].filter((f) => f.rel !== 'build.log');
  const bytes = files.reduce((n, f) => n + f.size, 0);
  if (bytes <= maxOutputBytes && files.length <= maxOutputFiles) return;
  const mb = (bytes / 1024 / 1024).toFixed(1);
  result.success = false; result.hasSite = false; result.hasPdf = false; result.hasPptx = false; result.hasThumbnail = false; result.hasPublicSite = false;
  result.hasNotes = false; result.hasText = false; result.hasSlideSheet = false;
  result.error = `Output too large: ${files.length} files, ${mb} MB (limit ${maxOutputFiles} files, ${Math.round(maxOutputBytes / 1024 / 1024)} MB)`;
  log(`FAILED: ${result.error}`);
  for (const name of readdirSync(outDir)) if (name !== 'build.log') rmSync(join(outDir, name), { recursive: true, force: true });
}

async function upload(outDir) {
  const files = [...walk(outDir)];
  log(`Uploading ${files.length} files`);
  if (uploadUrl.startsWith('file:') || /^[a-zA-Z]:[\\/]/.test(uploadUrl) || uploadUrl.startsWith('/')) {
    const dest = uploadUrl.startsWith('file:') ? fileURLToPath(uploadUrl) : uploadUrl;
    for (const f of files) { mkdirSync(dirname(join(dest, f.rel)), { recursive: true }); copyFileSync(f.path, join(dest, f.rel)); }
    return;
  }
  const { ContainerClient } = require('@azure/storage-blob');
  const container = new ContainerClient(uploadUrl);
  let i = 0;
  const workers = Array.from({ length: 8 }, async () => {
    while (i < files.length) {
      const f = files[i++];
      const blob = container.getBlockBlobClient(f.rel);
      const type = contentTypes[extname(f.rel).toLowerCase()] || 'application/octet-stream';
      await blob.uploadFile(f.path, { blobHTTPHeaders: { blobContentType: type } });
    }
  });
  await Promise.all(workers);
}

async function report(body) {
  // The web app may be scaling up from zero, or an ingress binding may still be propagating: retry with backoff.
  const delays = [0, 5000, 10000, 20000, 40000, 60000, 60000];
  let lastError;
  for (const delay of delays) {
    if (delay) await new Promise((r) => setTimeout(r, delay));
    try {
      const res = await fetch(callbackUrl, {
        method: 'POST',
        headers: { 'content-type': 'application/json', authorization: `Bearer ${callbackToken}` },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(30000),
      });
      if (res.ok) return;
      // 401 means the token is invalid/expired; retrying cannot help.
      if (res.status === 401) throw new Error(`Report rejected: HTTP ${res.status}`);
      lastError = new Error(`Report rejected: HTTP ${res.status}`);
    } catch (e) {
      if (String(e.message).startsWith('Report rejected: HTTP 401')) throw e;
      lastError = e;
      const cause = e && e.cause ? ` (${e.cause.code || ''} ${e.cause.message || ''})` : '';
      process.stdout.write(`Report attempt failed: ${scrub(e.message)}${scrub(cause)}; retrying\n`);
    }
  }
  throw lastError || new Error('Report failed');
}

// ---------------------------------------------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------------------------------------------
async function main() {
  const result = { success: false, hasSite: false, hasPdf: false, hasPptx: false, hasThumbnail: false, hasPublicSite: false, hasNotes: false, hasText: false, hasSlideSheet: false, slideCount: 0, annotations: result_annotations, error: null, warnings: [] };
  const repoDir = join(workRoot, 'repo');
  const outDir = join(workRoot, 'out');
  rmSync(repoDir, { recursive: true, force: true });
  rmSync(outDir, { recursive: true, force: true });
  mkdirForDeck(outDir); // slidev build/export (deck user) writes here

  const hardStop = setTimeout(async () => {
    log('Hard timeout reached');
    result.error = `Build exceeded ${timeoutSec}s`;
    await finish(outDir, result).catch(() => {});
    process.exit(2);
  }, remainingMs() + 20_000);

  try {
    log(`Podium build ${buildId}: ${slug} (${kind}) @ ${sha.slice(0, 7)} ${trusted ? '[trusted]' : '[untrusted]'}`);
    await checkout(repoDir);
    const deckDir = deckPath ? join(repoDir, ...deckPath.split('/')) : repoDir;
    if (!resolve(deckDir).startsWith(resolve(repoDir))) throw new Error('Deck path escapes repository');
    if (!existsSync(deckDir)) throw new Error(`Deck directory ${deckPath} not found at ${sha.slice(0, 7)}`);

    switch (kind) {
      case 'slidev': await buildSlidev(ctx, deckDir, outDir, result); break;
      case 'presenterm': await buildPresenterm(ctx, deckDir, outDir, result); break;
      case 'static': await buildStatic(ctx, deckDir, outDir, result); break;
      case 'powerpoint': await buildPowerPoint(ctx, deckDir, outDir, result); break;
      case 'pdf': await buildPdfDeck(ctx, deckDir, outDir, result); break;
      default: throw new Error(`Unsupported deck kind ${kind}`);
    }
    result.success = result.hasSite;
    if (result.hasSite) {
      await makeThumbnail(outDir, result);
      await makeSlideSheetAndText(outDir, result);
      writeManifest(outDir, result);
    }
  } catch (e) {
    result.error = scrub(e && e.message ? e.message : String(e));
    log(`FAILED: ${result.error}`);
  } finally {
    clearTimeout(hardStop);
  }
  await finish(outDir, result);
  process.exit(result.success ? 0 : 1);
}

let finished = false;
async function finish(outDir, result) {
  if (finished) return;
  finished = true;
  try {
    mkdirSync(outDir, { recursive: true });
    enforceOutputBudget(outDir, result);
    log(`Result: success=${result.success} site=${result.hasSite} pdf=${result.hasPdf} pptx=${result.hasPptx}`);
    writeFileSync(join(outDir, 'build.log'), logText());
    await upload(outDir);
  } catch (e) {
    result.success = false;
    result.error = result.error || scrub(`Upload failed: ${e.message}`);
    process.stdout.write(`Upload failed: ${scrub(e.message)}\n`);
  }
  try { await report(result); process.stdout.write('Reported.\n'); }
  catch (e) { process.stdout.write(`Report failed: ${scrub(e.message)}\n`); }
}

main().catch((e) => { process.stdout.write(`Fatal: ${scrub(e.stack || e)}\n`); process.exit(3); });