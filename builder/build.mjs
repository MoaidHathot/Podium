#!/usr/bin/env node
// Podium builder: runs inside an ephemeral container (or locally in development) and turns one deck at one commit
// into static artifacts, then uploads them and reports back. Configuration comes exclusively from PODIUM_* env vars
// (see BuilderEnvironment in Podium.Web). All secrets are removed from process.env before any deck code can run.

import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync, cpSync, readdirSync, statSync, lstatSync, copyFileSync, renameSync } from 'node:fs';
import { join, resolve, extname, relative, dirname, basename, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';
import { normalizeAnnotation, parsePresentermErrors } from './lib/annotations.mjs';

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
  // Decks in a sub-folder use a partial clone (no blobs) plus a cone sparse-checkout of that folder, so the download is
  // the deck and the files on its parent path, not every PowerPoint in the repository; the lazy blob fetch during
  // checkout reuses the same one-shot credential (git -c propagates to its child processes).
  const sparse = deckPath.length > 0;
  const gitTransfer = [...gitAuth, '-c', 'http.lowSpeedLimit=1000', '-c', 'http.lowSpeedTime=45', '-c', 'credential.helper='];
  const fetchArgs = [...gitTransfer, 'fetch', '-q', '--depth', '1', ...(sparse ? ['--filter=blob:none'] : []), 'origin', sha];
  for (let attempt = 1; ; attempt++) {
    // A killed fetch leaves lock files behind (shallow.lock, FETCH_HEAD.lock): every attempt starts from a fresh repo.
    if (attempt > 1) { rmSync(repoDir, { recursive: true, force: true }); mkdirForDeck(repoDir); }
    await run('git', ['init', '-q'], { cwd: repoDir, echo: attempt === 1 });
    await run('git', ['remote', 'add', 'origin', cleanUrl], { cwd: repoDir, echo: attempt === 1 });
    if (sparse) await run('git', ['sparse-checkout', 'set', '--cone', deckPath], { cwd: repoDir, echo: attempt === 1 });
    log(`$ git fetch --depth 1${sparse ? ' --filter=blob:none' : ''} origin ${sha.slice(0, 7)}${attempt > 1 ? ` (attempt ${attempt})` : ''}`);
    const r = await run('git', fetchArgs, { cwd: repoDir, echo: false, allowFail: true, timeoutMs: Math.min(remainingMs(), 4 * 60 * 1000), envExtra: { GCM_INTERACTIVE: 'never' } });
    if (r.code !== 0) { if (attempt >= 2) throw new Error(`git fetch failed (exit ${r.code})`); continue; }
    const c = await run('git', [...gitTransfer, 'checkout', '-q', 'FETCH_HEAD'], { cwd: repoDir, echo: false, allowFail: true, timeoutMs: Math.min(remainingMs(), 4 * 60 * 1000), envExtra: { GCM_INTERACTIVE: 'never' } });
    if (c.code === 0) break;
    if (attempt >= 2) throw new Error(`git checkout failed (exit ${c.code})`);
  }
  // Deck code runs during the build; make sure nothing sensitive is on disk when it does.
  rmSync(join(repoDir, '.git'), { recursive: true, force: true });
}

function readHeadmatter(mdPath) {
  try {
    const text = readFileSync(mdPath, 'utf8').replace(/\r\n/g, '\n');
    if (!text.startsWith('---\n')) return {};
    const end = text.indexOf('\n---', 4);
    if (end < 0) return {};
    const yaml = require('js-yaml');
    const fm = yaml.load(text.slice(4, end));
    return fm && typeof fm === 'object' ? fm : {};
  } catch (e) {
    log(`Could not parse headmatter: ${e.message}`);
    return {};
  }
}

async function ensureSlidevProject(deckDir) {
  const pkgPath = join(deckDir, 'package.json');
  const podiumModules = resolve(here, 'node_modules');
  if (!existsSync(pkgPath)) {
    log('No package.json: using the builder\'s bundled Slidev');
    writeFileSync(pkgPath, JSON.stringify({ name: 'podium-deck', private: true, type: 'module' }, null, 2));
    ownForDeck(pkgPath);
    // Reuse the builder's own dependencies rather than hitting the network for an unpinned install. They must be a
    // real directory inside the deck: through a symlink Vite resolves themes to their real path outside the project
    // root and leaves import.meta.glob() calls untransformed, which breaks the built deck at runtime. The copy is made
    // by the deck user so Slidev can write its caches (node_modules/.slidev) into it.
    const t0 = Date.now();
    await copyTreeForDeck(podiumModules, join(deckDir, 'node_modules'));
    log(`Copied bundled dependencies in ${Math.round((Date.now() - t0) / 1000)}s`);
    return;
  }
  const hasLock = existsSync(join(deckDir, 'package-lock.json')) || existsSync(join(deckDir, 'npm-shrinkwrap.json'));
  // Decided by the web app from .podium.yml (trusted repositories only) and passed down; the builder never reads
  // deck config itself, so one place owns the policy.
  const allowScripts = trusted && env.PODIUM_NPM_SCRIPTS === '1';
  const args = [hasLock ? 'ci' : 'install', '--no-audit', '--no-fund', '--loglevel', 'error', '--prefer-offline'];
  if (!allowScripts) args.push('--ignore-scripts');
  await run(npmCmd, args, { cwd: deckDir, envExtra: { PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD: '1' } });
  // Slidev export needs playwright-chromium resolvable from the deck; fall back to the builder's copy.
  for (const dep of ['playwright-chromium', 'playwright-core']) {
    const target = join(deckDir, 'node_modules', dep);
    if (!existsSync(target) && existsSync(join(podiumModules, dep))) linkDir(join(podiumModules, dep), target);
  }
}

function linkDir(from, to) {
  mkdirSync(dirname(to), { recursive: true });
  try {
    require('node:fs').symlinkSync(from, to, 'junction');
  } catch {
    cpSync(from, to, { recursive: true, dereference: true });
  }
}


async function ensurePlaywrightBrowser(deckDir) {
  const cli = join(deckDir, 'node_modules', 'playwright-core', 'cli.js');
  if (!existsSync(cli)) { log('playwright-core not found; skipping browser check'); return false; }
  // No-op when the revision expected by this playwright-core is already present in PLAYWRIGHT_BROWSERS_PATH.
  const r = await run(process.execPath, [cli, 'install', 'chromium'], { cwd: deckDir, allowFail: true, timeoutMs: Math.min(remainingMs(), 6 * 60 * 1000) });
  return r.code === 0;
}

async function buildSlidev(deckDir, outDir, result) {
  const entryPath = join(deckDir, entry);
  if (!existsSync(entryPath)) throw new Error(`Entry ${entry} not found in ${deckPath || '/'}`);
  const fm = readHeadmatter(entryPath);
  await ensureSlidevProject(deckDir);
  const addonInjected = injectPodiumAddon(deckDir, entryPath);
  const addonProtocol = addonInjected ? ADDON_PROTOCOL : 0;

  const slidevBin = resolveSlidevBin(deckDir);
  const site = join(outDir, 'site');
  await run(process.execPath, [slidevBin, 'build', entry, '--base', basePath, '--out', site], { cwd: deckDir, envExtra: { NODE_OPTIONS: '--max-old-space-size=1536' } });
  if (!existsSync(join(site, 'index.html'))) throw new Error('Slidev build produced no index.html');
  result.hasSite = true;
  injectPodiumMeta(join(site, 'index.html'), addonProtocol);
  await extractNotesAndLint(deckDir, outDir, result);

  if (stripNotes) {
    // Second variant for viewers: identical build with speaker notes emptied at compile time (`--without-notes`), so
    // the notes never reach a browser that is not the presenter's. Served from site-public/ by the web app.
    const publicSite = join(outDir, 'site-public');
    const r = await run(process.execPath, [slidevBin, 'build', entry, '--base', basePath, '--out', publicSite, '--without-notes'], { cwd: deckDir, allowFail: true, envExtra: { NODE_OPTIONS: '--max-old-space-size=1536' }, timeoutMs: Math.min(remainingMs(), 6 * 60 * 1000) });
    if (r.code === 0 && existsSync(join(publicSite, 'index.html'))) { injectPodiumMeta(join(publicSite, 'index.html'), addonProtocol); result.hasPublicSite = true; }
    else result.warnings.push('Notes-free copy for viewers could not be built; viewers get the full build (see build log)');
  }

  if (exportPdf || exportPptx) {
    const browserOk = await ensurePlaywrightBrowser(deckDir);
    if (!browserOk) result.warnings.push('Chromium unavailable; PDF/PPTX export skipped');
    else {
      // Slidev's exporter boots a Vite dev server; the first run pays for dependency optimisation and can miss
      // Playwright's fixed 30s element timeout on small machines. A second attempt reuses the warm cache.
      const exportWithRetry = async (args, outFile) => {
        for (let attempt = 1; attempt <= 2; attempt++) {
          const r = await run(process.execPath, [slidevBin, 'export', entry, ...args, '--output', outFile, '--timeout', '120000'], { cwd: deckDir, allowFail: true, timeoutMs: Math.min(remainingMs(), 8 * 60 * 1000) });
          if (r.code === 0 && existsSync(outFile)) return true;
          if (attempt === 1) log('Export failed on first attempt; retrying once with a warm cache');
        }
        return false;
      };
      if (exportPdf) {
        if (await exportWithRetry([], join(outDir, 'deck.pdf'))) {
          result.hasPdf = true;
          // Make Slidev's own "download PDF" button (headmatter download: true) work inside the served site.
          const name = typeof fm.exportFilename === 'string' && fm.exportFilename ? `${fm.exportFilename}.pdf` : 'slidev-exported.pdf';
          if (fm.download) copyFileSync(join(outDir, 'deck.pdf'), join(site, name));
        } else result.warnings.push('PDF export failed (see build log)');
      }
      if (exportPptx) {
        if (await exportWithRetry(['--format', 'pptx'], join(outDir, 'deck.pptx'))) result.hasPptx = true;
        else result.warnings.push('PPTX export failed (see build log)');
      }
    }
  }
}

/**
 * Speaker notes (for the phone remote) and deck-health findings, produced by notes.mjs running as the deck user with
 * the deck's own @slidev/parser. Notes are a separate artifact served only to presenters; the site itself may be the
 * notes-free variant for everyone else.
 */
async function extractNotesAndLint(deckDir, outDir, result) {
  const script = join(here, 'notes.mjs');
  if (!existsSync(join(deckDir, 'node_modules', '@slidev', 'parser'))) { log('No @slidev/parser in the deck; skipping notes'); return; }
  const notesOut = join(outDir, 'notes.json');
  const lintOut = join(workRoot, 'lint.json');
  mkdirForDeck(dirname(lintOut));
  const r = await run(process.execPath, [script, deckDir, entry, join(workRoot, 'repo'), deckPath, notesOut, lintOut, join(outDir, 'text.json')], { cwd: deckDir, allowFail: true, timeoutMs: Math.min(remainingMs(), 60000) });
  if (r.code === 0 && existsSync(notesOut)) {
    try {
      const notes = JSON.parse(readFileSync(notesOut, 'utf8'));
      if (Array.isArray(notes)) { result.hasNotes = true; result.slideCount = result.slideCount || notes.length; }
    } catch { rmSync(notesOut, { force: true }); }
  } else result.warnings.push('Speaker notes could not be extracted (see build log)');
  if (existsSync(lintOut)) {
    try {
      const lint = JSON.parse(readFileSync(lintOut, 'utf8'));
      if (Array.isArray(lint)) for (const a of lint.slice(0, 50)) addAnnotation(a.path, a.line, a.level, a.message);
    } catch { /* deck-controlled output; ignore garbage */ }
  }
}

const result_annotations = [];
function addAnnotation(path, line, level, message) {
  const a = normalizeAnnotation({ path, line, level, message });
  if (a && result_annotations.length < 50) result_annotations.push(a);
}

/** presenterm reports "error at main.md:34:1: could not load image 'x.png'": turn that into a positioned finding. */
function annotatePresentermErrors(output) {
  for (const a of parsePresentermErrors(output, deckPath, entry)) addAnnotation(a.path, a.line, a.level, a.message);
}

function resolveSlidevBin(deckDir) {
  const candidates = [
    join(deckDir, 'node_modules', '@slidev', 'cli', 'bin', 'slidev.mjs'),
    join(here, 'node_modules', '@slidev', 'cli', 'bin', 'slidev.mjs'),
  ];
  for (const c of candidates) if (existsSync(c)) return c;
  throw new Error('Slidev CLI not found (deck has no @slidev/cli and the builder has no bundled copy)');
}

// Protocol version of the bundled sync addon / bridge. Podium's live-ui.js stays inert on older builds, whose addon
// still draws its own pill, so a deck is never decorated twice during the rebuild wave after an upgrade.
const ADDON_PROTOCOL = 3.2;

function injectPodiumMeta(indexHtml, addonProtocol = ADDON_PROTOCOL) {
  const html = readFileSync(indexHtml, 'utf8');
  if (html.includes('name="podium-build"')) return;
  writeFileSync(indexHtml, html.replace('<head>', `<head><meta name="podium-build" content="${buildId}"><meta name="podium-slug" content="${slug}"><meta name="podium-addon" content="${addonProtocol}">`));
}

/**
 * Adds the Podium sync addon (cross-device presenter sync) to the deck's headmatter. Only the headmatter block is
 * re-serialised; the slide content is left byte-for-byte intact. Works on the ephemeral checkout only.
 */
function injectPodiumAddon(deckDir, entryPath) {
  const source = join(here, 'addon');
  if (!existsSync(join(source, 'setup', 'root.ts'))) { log('Podium addon not bundled with this builder; skipping sync addon'); return false; }
  const target = join(deckDir, '.podium-addon');
  rmSync(target, { recursive: true, force: true });
  cpSync(source, target, { recursive: true });

  const yaml = require('js-yaml');
  const text = readFileSync(entryPath, 'utf8');
  const normalized = text.replace(/\r\n/g, '\n');
  let fm = {};
  let body = normalized;
  if (normalized.startsWith('---\n')) {
    const end = normalized.indexOf('\n---', 4);
    if (end >= 0) {
      try { fm = yaml.load(normalized.slice(4, end), { schema: yaml.CORE_SCHEMA }) || {}; } catch (e) { log(`Cannot parse headmatter (${e.message}); sync addon not injected`); return false; }
      // Keep whatever follows the closing fence (usually a newline) exactly as it was.
      body = normalized.slice(end + 4);
    }
  }
  if (typeof fm !== 'object' || Array.isArray(fm)) { log('Unexpected headmatter shape; sync addon not injected'); return false; }
  const addons = Array.isArray(fm.addons) ? fm.addons : (typeof fm.addons === 'string' ? [fm.addons] : []);
  // '@/' is Slidev's syntax for a path relative to the deck root (absolute Windows paths are rejected as addon names).
  const ref = '@/.podium-addon';
  if (!addons.includes(ref)) addons.push(ref);
  fm.addons = addons;
  const dumped = yaml.dump(fm, { lineWidth: -1, noRefs: true, schema: yaml.CORE_SCHEMA });
  writeFileSync(entryPath, `---\n${dumped}---${body}`);
  log(`Injected Podium sync addon (protocol ${ADDON_PROTOCOL})`);
  return true;
}

async function buildPresenterm(deckDir, outDir, result) {
  const entryPath = join(deckDir, entry);
  if (!existsSync(entryPath)) throw new Error(`Entry ${entry} not found`);
  const site = join(outDir, 'site');
  mkdirForDeck(site); // presenterm (deck user) writes here
  const html = join(site, 'index.html');
  // presenterm's HTML export still probes the terminal (capability query + terminal size) in 0.16. Without a TTY the
  // size lookup fails ("Inappropriate ioctl"), and with a bare pty the query blocks forever. Giving it explicit export
  // dimensions and a pinned image protocol sidesteps both. The deck's own config.yaml is merged in so the exported
  // theme matches what the author sees locally.
  const configPath = writePresentermConfig(deckDir);
  const r = await run('presenterm', ['--export-html', entry, '--output', html, '--config-file', configPath, '--image-protocol', 'ascii-blocks'],
    { cwd: deckDir, allowFail: true, envExtra: { TERM: 'xterm-256color', COLUMNS: '120', LINES: '30' }, timeoutMs: Math.min(remainingMs(), 5 * 60 * 1000) });
  const base = entry.replace(/\.md$/i, '');
  if (r.code === 0 && existsSync(html)) result.hasSite = true;
  else {
    annotatePresentermErrors(r.out || '');
    // Fall back to an HTML export committed next to the source.
    const committed = findSibling(deckDir, base, '.html');
    if (committed) { copyFileSync(committed, html); result.hasSite = true; result.warnings.push('presenterm export failed; served the committed HTML export instead'); }
    else throw new Error('presenterm HTML export failed and no committed .html export exists');
  }
  if (exportPdf) {
    // A fresh export tracks the source; presenterm drives weasyprint (shipped in the builder image) for the PDF.
    // A PDF committed next to the deck is the fallback when the export fails or weasyprint is missing (local dev).
    const pdf = join(outDir, 'deck.pdf');
    const p = await run('presenterm', ['--export-pdf', entry, '--output', pdf, '--config-file', configPath, '--image-protocol', 'ascii-blocks'],
      { cwd: deckDir, allowFail: true, envExtra: { TERM: 'xterm-256color', COLUMNS: '120', LINES: '30' }, timeoutMs: Math.min(remainingMs(), 5 * 60 * 1000) });
    const committedPdf = findSibling(deckDir, base, '.pdf');
    if (p.code === 0 && existsSync(pdf) && statSync(pdf).size > 0) result.hasPdf = true;
    else if (committedPdf) { copyFileSync(committedPdf, pdf); result.hasPdf = true; result.warnings.push('presenterm PDF export failed; served the committed PDF instead'); }
    else { rmSync(pdf, { force: true }); result.warnings.push('No PDF: presenterm PDF export failed (see build log); commit an exported PDF next to the deck to serve one'); }
  }
}


function writePresentermConfig(deckDir) {
  const yaml = require('js-yaml');
  let config = {};
  for (const name of ['config.yaml', 'config.yml']) {
    const p = join(deckDir, name);
    if (existsSync(p)) {
      try { config = yaml.load(readFileSync(p, 'utf8')) || {}; log(`Using deck ${name}`); }
      catch (e) { log(`Ignoring invalid ${name}: ${e.message}`); }
      break;
    }
  }
  if (typeof config !== 'object' || Array.isArray(config)) config = {};
  config.export = config.export && typeof config.export === 'object' ? config.export : {};
  config.export.dimensions = config.export.dimensions || { rows: 30, columns: 120 };
  const out = join(workRoot, 'presenterm-config.yaml');
  writeFileSync(out, yaml.dump(config, { lineWidth: -1 }));
  return out;
}

function findSibling(dir, base, ext) {
  const exact = join(dir, base + ext);
  if (existsSync(exact)) return exact;
  const any = readdirSync(dir).filter((f) => f.toLowerCase().endsWith(ext)).sort();
  return any.length ? join(dir, any[0]) : null;
}

async function buildStatic(deckDir, outDir, result) {
  const site = join(outDir, 'site');
  mkdirSync(site, { recursive: true });
  for (const name of readdirSync(deckDir)) {
    if (name === 'node_modules' || name.startsWith('.')) continue;
    cpSync(join(deckDir, name), join(site, name), { recursive: true });
  }
  const entryFile = join(site, entry);
  if (!existsSync(entryFile)) throw new Error(`Entry ${entry} not found`);
  if (basename(entryFile).toLowerCase() !== 'index.html') copyFileSync(entryFile, join(site, 'index.html'));
  result.hasSite = true;
  if (exportPdf) {
    const pdf = findSibling(deckDir, entry.replace(/\.[^.]+$/, ''), '.pdf');
    if (pdf) { copyFileSync(pdf, join(outDir, 'deck.pdf')); result.hasPdf = true; }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// PowerPoint / PDF decks
// ---------------------------------------------------------------------------------------------------------------
function which(cmd) {
  const dirs = (process.env.PATH || '').split(process.platform === 'win32' ? ';' : ':');
  const exts = process.platform === 'win32' ? ['.exe', '.cmd', '.bat', ''] : [''];
  for (const d of dirs) for (const e of exts) { const p = join(d, cmd + e); if (d && existsSync(p)) return p; }
  return null;
}

async function buildPowerPoint(deckDir, outDir, result) {
  const src = join(deckDir, entry);
  if (!existsSync(src)) throw new Error(`Entry ${entry} not found`);
  copyFileSync(src, join(outDir, 'deck.pptx'));
  result.hasPptx = true;

  // Prefer an export the author committed next to the file (PowerPoint's own PDF beats a LibreOffice conversion).
  const stem = entry.replace(/\.[^.]+$/, '');
  const committedPdf = join(deckDir, stem + '.pdf');
  if (existsSync(committedPdf)) {
    copyFileSync(committedPdf, join(outDir, 'deck.pdf'));
    result.hasPdf = true;
    log('Using the committed PDF next to the presentation');
  } else {
    const soffice = which('soffice') || which('libreoffice');
    if (!soffice) result.warnings.push('LibreOffice not available: the deck can be downloaded but not viewed in the browser');
    else {
      const tmp = join(workRoot, 'soffice-out');
      rmSync(tmp, { recursive: true, force: true }); mkdirForDeck(tmp);
      const r = await run(soffice, ['--headless', '--norestore', '--nologo', `-env:UserInstallation=file://${join(workRoot, 'soffice-profile').replace(/\\/g, '/')}`, '--convert-to', 'pdf', '--outdir', tmp, src],
        { cwd: deckDir, allowFail: true, timeoutMs: Math.min(remainingMs(), 6 * 60 * 1000), envExtra: { HOME: workRoot } });
      const produced = existsSync(tmp) ? readdirSync(tmp).find((f) => f.toLowerCase().endsWith('.pdf')) : null;
      if (r.code === 0 && produced) { copyFileSync(join(tmp, produced), join(outDir, 'deck.pdf')); result.hasPdf = true; }
      else result.warnings.push('PDF conversion failed (see build log); the PowerPoint file can still be downloaded');
    }
  }
  await writeViewerSite(outDir, result, basename(entry));
}

async function buildPdfDeck(deckDir, outDir, result) {
  const src = join(deckDir, entry);
  if (!existsSync(src)) throw new Error(`Entry ${entry} not found`);
  copyFileSync(src, join(outDir, 'deck.pdf'));
  result.hasPdf = true;
  await writeViewerSite(outDir, result, basename(entry));
}

/**
 * The viewer site for PowerPoint/PDF decks. Pages are rendered to JPEGs (pdftoppm) and shown by Podium's own viewer
 * (/_podium/pages.js), which gives these decks what Slidev decks have: keyboard/touch navigation, deep links to a
 * page, cross-device sync, the phone remote, blackout, offline cache and the live session UI. Without pdftoppm (or
 * without a PDF) the browser's PDF viewer is used in an iframe, as before.
 */
async function writeViewerSite(outDir, result, fileName) {
  const site = join(outDir, 'site');
  mkdirSync(site, { recursive: true });
  const pdf = join(outDir, 'deck.pdf');
  const hasPdf = existsSync(pdf);
  if (hasPdf) copyFileSync(pdf, join(site, 'deck.pdf'));
  const title = escapeHtml(env.PODIUM_DECK_TITLE || fileName);
  const pages = hasPdf ? await renderPages(pdf, join(site, 'pages')) : null;
  const metas = `<meta name="podium-build" content="${buildId}"><meta name="podium-slug" content="${slug}"><meta name="podium-addon" content="${pages ? ADDON_PROTOCOL : 0}">`;
  let body;
  if (pages) {
    // The viewer runtime (/_podium/pages.js) and the sync bridge are injected by the server when the page is served.
    const data = escapeHtml(JSON.stringify(pages));
    body = `<main id="podium-pages" class="podium-pages" data-pages="${data}" aria-label="${title}" tabindex="0"></main>
<noscript><p style="padding:2rem">This presentation needs JavaScript. <a href="deck.pdf">Open the PDF</a> instead.</p></noscript>`;
    result.slideCount = result.slideCount || pages.count;
  } else if (hasPdf) {
    body = `<iframe src="deck.pdf#view=Fit&amp;pagemode=none" title="${title}" allowfullscreen></iframe>`;
  } else {
    body = `<main><h1>${title}</h1><p>This presentation could not be rendered in the browser.</p><p><a href="/d/${slug}.pptx">Download the PowerPoint file</a></p></main>`;
  }
  writeFileSync(join(site, 'index.html'), `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
${metas}
<title>${title}</title>
<style>html,body{margin:0;height:100%;background:#000;color:#e6e9f0;font-family:system-ui,sans-serif}iframe{border:0;width:100%;height:100%;display:block}main.podium-pages{position:fixed;inset:0;outline:0}main:not(.podium-pages){max-width:40rem;margin:15vh auto;padding:0 1.5rem}a{color:#7c9cff}</style>
</head><body>${body}</body></html>
`);
  result.hasSite = true;
}

/** Renders every PDF page to <dir>/001.jpg ... at projector resolution. Returns {count,width,height} or null. */
async function renderPages(pdf, dir) {
  const pdftoppm = which('pdftoppm');
  if (!pdftoppm) { log('pdftoppm not available; the PDF is shown with the browser viewer'); return null; }
  let count = 0, width = 0, height = 0;
  const pdfinfo = which('pdfinfo');
  if (pdfinfo) {
    const info = await run(pdfinfo, [pdf], { allowFail: true, echo: false, timeoutMs: 30000 });
    const m = /Pages:\s+(\d+)/.exec(info.out || '');
    if (m) count = Number(m[1]);
    const size = /Page size:\s+([\d.]+) x ([\d.]+)/.exec(info.out || '');
    if (size) { width = Number(size[1]); height = Number(size[2]); }
  }
  if (count > SHEET_MAX_PAGES) { log(`${count} pages exceed the ${SHEET_MAX_PAGES}-page viewer limit; the PDF is shown with the browser viewer`); return null; }
  rmSync(dir, { recursive: true, force: true });
  mkdirForDeck(dir);
  const r = await run(pdftoppm, ['-jpeg', '-jpegopt', 'quality=80', '-scale-to-x', '1920', '-scale-to-y', '-1', pdf, join(dir, 'p')], { allowFail: true, echo: false, timeoutMs: Math.min(remainingMs(), 5 * 60 * 1000) });
  const produced = existsSync(dir) ? readdirSync(dir).filter((f) => /^p-\d+\.jpg$/.test(f)).sort((a, b) => a.localeCompare(b, undefined, { numeric: true })) : [];
  if (r.code !== 0 || !produced.length) { log('Page rendering failed; the PDF is shown with the browser viewer'); rmSync(dir, { recursive: true, force: true }); return null; }
  produced.forEach((f, i) => renameSync(join(dir, f), join(dir, `${String(i + 1).padStart(3, '0')}.jpg`)));
  const pages = { count: produced.length, aspect: width > 0 && height > 0 ? Number((width / height).toFixed(4)) : null };
  writeFileSync(join(dir, 'index.json'), JSON.stringify(pages));
  log(`Rendered ${pages.count} pages for the viewer`);
  return pages;
}

function escapeHtml(s) { return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

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
const SHEET_COLS = 6;
const SHEET_MAX_PAGES = 400;

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
      case 'slidev': await buildSlidev(deckDir, outDir, result); break;
      case 'presenterm': await buildPresenterm(deckDir, outDir, result); break;
      case 'static': await buildStatic(deckDir, outDir, result); break;
      case 'powerpoint': await buildPowerPoint(deckDir, outDir, result); break;
      case 'pdf': await buildPdfDeck(deckDir, outDir, result); break;
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