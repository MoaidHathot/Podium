#!/usr/bin/env node
// Podium builder: runs inside an ephemeral container (or locally in development) and turns one deck at one commit
// into static artifacts, then uploads them and reports back. Configuration comes exclusively from PODIUM_* env vars
// (see BuilderEnvironment in Podium.Web). All secrets are removed from process.env before any deck code can run.

import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync, cpSync, readdirSync, statSync, copyFileSync } from 'node:fs';
import { join, resolve, extname, relative, dirname, basename, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const here = dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------------------------------------------
// Inputs (read once, then scrubbed from the environment)
// ---------------------------------------------------------------------------------------------------------------
const env = {};
for (const [k, v] of Object.entries(process.env)) {
  if (k.startsWith('PODIUM_')) { env[k] = v; delete process.env[k]; }
}
const need = (k) => { if (!env[k]) throw new Error(`Missing ${k}`); return env[k]; };

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
const logLines = [];
const scrub = (s) => secrets.reduce((acc, sec) => (sec ? acc.split(sec).join('***') : acc), String(s));
function log(msg) {
  const line = `[${new Date().toISOString()}] ${scrub(msg)}`;
  logLines.push(line);
  process.stdout.write(line + '\n');
}

// ---------------------------------------------------------------------------------------------------------------
// Process helpers
// ---------------------------------------------------------------------------------------------------------------
const deadline = Date.now() + Math.max(60, timeoutSec - 30) * 1000;
const remainingMs = () => Math.max(1000, deadline - Date.now());

function run(cmd, args, { cwd, envExtra = {}, allowFail = false, timeoutMs, echo = true } = {}) {
  return new Promise((resolvePromise, reject) => {
    const display = `${cmd} ${args.map((a) => (a.includes(' ') ? JSON.stringify(a) : a)).join(' ')}`;
    if (echo) log(`$ ${display}`);
    const child = spawn(cmd, args, {
      cwd,
      env: { ...process.env, ...envExtra, CI: '1', FORCE_COLOR: '0', NO_COLOR: '1', GIT_TERMINAL_PROMPT: '0' },
      stdio: ['ignore', 'pipe', 'pipe'],
      shell: process.platform === 'win32' && /\.(cmd|bat)$/i.test(cmd),
    });
    let out = '';
    const onData = (d) => { const s = d.toString(); out += s; for (const l of s.split(/\r?\n/)) if (l.trim()) log(`  ${l}`); };
    child.stdout.on('data', onData);
    child.stderr.on('data', onData);
    const t = setTimeout(() => { log(`Timeout after ${Math.round((timeoutMs ?? remainingMs()) / 1000)}s, killing ${cmd}`); child.kill('SIGKILL'); }, timeoutMs ?? remainingMs());
    child.on('error', (e) => { clearTimeout(t); reject(e); });
    child.on('close', (code) => {
      clearTimeout(t);
      if (code === 0 || allowFail) resolvePromise({ code, out });
      else reject(new Error(`${cmd} exited with code ${code}`));
    });
  });
}

const npmCmd = process.platform === 'win32' ? 'npm.cmd' : 'npm';
const npxCmd = process.platform === 'win32' ? 'npx.cmd' : 'npx';

// ---------------------------------------------------------------------------------------------------------------
// Steps
// ---------------------------------------------------------------------------------------------------------------
async function checkout(repoDir) {
  mkdirSync(repoDir, { recursive: true });
  const u = new URL(cloneUrl);
  const authHeader = u.username || u.password ? `Authorization: Basic ${Buffer.from(`${decodeURIComponent(u.username)}:${decodeURIComponent(u.password)}`).toString('base64')}` : null;
  u.username = ''; u.password = '';
  const cleanUrl = u.toString();
  const gitAuth = authHeader ? ['-c', `http.extraHeader=${authHeader}`] : [];
  secrets.push(...(authHeader ? [authHeader.slice('Authorization: Basic '.length)] : []));

  await run('git', ['init', '-q'], { cwd: repoDir });
  await run('git', ['remote', 'add', 'origin', cleanUrl], { cwd: repoDir });
  // Fetch exactly the requested commit; the credential lives only on this command line, never in .git/config.
  await run('git', [...gitAuth, 'fetch', '-q', '--depth', '1', 'origin', sha], { cwd: repoDir, echo: false });
  log(`$ git fetch --depth 1 origin ${sha.slice(0, 7)}`);
  await run('git', ['checkout', '-q', 'FETCH_HEAD'], { cwd: repoDir });
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
    // Reuse the builder's own dependencies rather than hitting the network for an unpinned install.
    linkDir(join(podiumModules), join(deckDir, 'node_modules'));
    return;
  }
  const hasLock = existsSync(join(deckDir, 'package-lock.json')) || existsSync(join(deckDir, 'npm-shrinkwrap.json'));
  const allowScripts = trusted && readPodiumConfig(deckDir).npmScripts === true;
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

function readPodiumConfig(deckDir) {
  for (const name of ['.podium.yml', '.podium.yaml', 'podium.yml']) {
    const p = join(deckDir, name);
    if (existsSync(p)) {
      try { return require('js-yaml').load(readFileSync(p, 'utf8')) || {}; } catch (e) { log(`Ignoring invalid ${name}: ${e.message}`); }
    }
  }
  return {};
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
  injectPodiumAddon(deckDir, entryPath);

  const slidevBin = resolveSlidevBin(deckDir);
  const site = join(outDir, 'site');
  await run(process.execPath, [slidevBin, 'build', entry, '--base', basePath, '--out', site], { cwd: deckDir, envExtra: { NODE_OPTIONS: '--max-old-space-size=1536' } });
  if (!existsSync(join(site, 'index.html'))) throw new Error('Slidev build produced no index.html');
  result.hasSite = true;
  injectPodiumMeta(join(site, 'index.html'));

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

function resolveSlidevBin(deckDir) {
  const candidates = [
    join(deckDir, 'node_modules', '@slidev', 'cli', 'bin', 'slidev.mjs'),
    join(here, 'node_modules', '@slidev', 'cli', 'bin', 'slidev.mjs'),
  ];
  for (const c of candidates) if (existsSync(c)) return c;
  throw new Error('Slidev CLI not found (deck has no @slidev/cli and the builder has no bundled copy)');
}

function injectPodiumMeta(indexHtml) {
  const html = readFileSync(indexHtml, 'utf8');
  if (html.includes('name="podium-build"')) return;
  writeFileSync(indexHtml, html.replace('<head>', `<head><meta name="podium-build" content="${buildId}"><meta name="podium-slug" content="${slug}">`));
}

/**
 * Adds the Podium sync addon (cross-device presenter sync) to the deck's headmatter. Only the headmatter block is
 * re-serialised; the slide content is left byte-for-byte intact. Works on the ephemeral checkout only.
 */
function injectPodiumAddon(deckDir, entryPath) {
  const source = join(here, 'addon');
  if (!existsSync(join(source, 'setup', 'root.ts'))) { log('Podium addon not bundled with this builder; skipping sync addon'); return; }
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
      try { fm = yaml.load(normalized.slice(4, end), { schema: yaml.CORE_SCHEMA }) || {}; } catch (e) { log(`Cannot parse headmatter (${e.message}); sync addon not injected`); return; }
      // Keep whatever follows the closing fence (usually a newline) exactly as it was.
      body = normalized.slice(end + 4);
    }
  }
  if (typeof fm !== 'object' || Array.isArray(fm)) { log('Unexpected headmatter shape; sync addon not injected'); return; }
  const addons = Array.isArray(fm.addons) ? fm.addons : (typeof fm.addons === 'string' ? [fm.addons] : []);
  // '@/' is Slidev's syntax for a path relative to the deck root (absolute Windows paths are rejected as addon names).
  const ref = '@/.podium-addon';
  if (!addons.includes(ref)) addons.push(ref);
  fm.addons = addons;
  const dumped = yaml.dump(fm, { lineWidth: -1, noRefs: true, schema: yaml.CORE_SCHEMA });
  writeFileSync(entryPath, `---\n${dumped}---${body}`);
  log('Injected Podium sync addon');
}

async function buildPresenterm(deckDir, outDir, result) {
  const entryPath = join(deckDir, entry);
  if (!existsSync(entryPath)) throw new Error(`Entry ${entry} not found`);
  const site = join(outDir, 'site');
  mkdirSync(site, { recursive: true });
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
    // Fall back to an HTML export committed next to the source.
    const committed = findSibling(deckDir, base, '.html');
    if (committed) { copyFileSync(committed, html); result.hasSite = true; result.warnings.push('presenterm export failed; served the committed HTML export instead'); }
    else throw new Error('presenterm HTML export failed and no committed .html export exists');
  }
  if (exportPdf) {
    const committedPdf = findSibling(deckDir, base, '.pdf');
    if (committedPdf) { copyFileSync(committedPdf, join(outDir, 'deck.pdf')); result.hasPdf = true; }
    else result.warnings.push('No PDF: presenterm PDF export needs weasyprint; commit an exported PDF next to the deck to serve one');
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
    if (statSync(p).isDirectory()) yield* walk(p, base);
    else yield { path: p, rel: relative(base, p).split(sep).join('/') };
  }
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
  const result = { success: false, hasSite: false, hasPdf: false, hasPptx: false, error: null, warnings: [] };
  const repoDir = join(workRoot, 'repo');
  const outDir = join(workRoot, 'out');
  rmSync(repoDir, { recursive: true, force: true });
  rmSync(outDir, { recursive: true, force: true });
  mkdirSync(outDir, { recursive: true });

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
      default: throw new Error(`Unsupported deck kind ${kind}`);
    }
    result.success = result.hasSite;
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
    log(`Result: success=${result.success} site=${result.hasSite} pdf=${result.hasPdf} pptx=${result.hasPptx}`);
    mkdirSync(outDir, { recursive: true });
    writeFileSync(join(outDir, 'build.log'), logLines.join('\n') + '\n');
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