// Slidev decks: npm install (or the bundled Slidev), the Podium sync addon injected into the headmatter, slidev build
// (plus a notes-free copy for viewers), speaker notes + deck-health lint through the deck's own parser, PDF/PPTX export.
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync, cpSync, copyFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { ADDON_PROTOCOL, podiumMetaTags } from '../lib/shared.mjs';

export async function buildSlidev(ctx, deckDir, outDir, result) {
  const { entry, deckPath, basePath, exportPdf, exportPptx, stripNotes, log, run, remainingMs } = ctx;
  const entryPath = join(deckDir, entry);
  if (!existsSync(entryPath)) throw new Error(`Entry ${entry} not found in ${deckPath || '/'}`);
  const fm = readHeadmatter(ctx, entryPath);
  await ensureSlidevProject(ctx, deckDir);
  const addonInjected = injectPodiumAddon(ctx, deckDir, entryPath);
  const addonProtocol = addonInjected ? ADDON_PROTOCOL : 0;

  const slidevBin = resolveSlidevBin(ctx, deckDir);
  const site = join(outDir, 'site');
  await run(process.execPath, [slidevBin, 'build', entry, '--base', basePath, '--out', site], { cwd: deckDir, envExtra: { NODE_OPTIONS: '--max-old-space-size=1536' } });
  if (!existsSync(join(site, 'index.html'))) throw new Error('Slidev build produced no index.html');
  result.hasSite = true;
  injectPodiumMeta(ctx, join(site, 'index.html'), addonProtocol);
  await extractNotesAndLint(ctx, deckDir, outDir, result);

  if (stripNotes) {
    // Second variant for viewers: identical build with speaker notes emptied at compile time (`--without-notes`), so
    // the notes never reach a browser that is not the presenter's. Served from site-public/ by the web app.
    const publicSite = join(outDir, 'site-public');
    const r = await run(process.execPath, [slidevBin, 'build', entry, '--base', basePath, '--out', publicSite, '--without-notes'], { cwd: deckDir, allowFail: true, envExtra: { NODE_OPTIONS: '--max-old-space-size=1536' }, timeoutMs: Math.min(remainingMs(), 6 * 60 * 1000) });
    if (r.code === 0 && existsSync(join(publicSite, 'index.html'))) { injectPodiumMeta(ctx, join(publicSite, 'index.html'), addonProtocol); result.hasPublicSite = true; }
    else result.warnings.push('Notes-free copy for viewers could not be built; viewers get the full build (see build log)');
  }

  if (exportPdf || exportPptx) {
    const browserOk = await ensurePlaywrightBrowser(ctx, deckDir);
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

function readHeadmatter(ctx, mdPath) {
  try {
    const text = readFileSync(mdPath, 'utf8').replace(/\r\n/g, '\n');
    if (!text.startsWith('---\n')) return {};
    const end = text.indexOf('\n---', 4);
    if (end < 0) return {};
    const yaml = ctx.require('js-yaml');
    const fm = yaml.load(text.slice(4, end));
    return fm && typeof fm === 'object' ? fm : {};
  } catch (e) {
    ctx.log(`Could not parse headmatter: ${e.message}`);
    return {};
  }
}

async function ensureSlidevProject(ctx, deckDir) {
  const { here, log, run, trusted, env, npmCmd, ownForDeck, copyTreeForDeck } = ctx;
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
    if (!existsSync(target) && existsSync(join(podiumModules, dep))) linkDir(ctx, join(podiumModules, dep), target);
  }
}

function linkDir(ctx, from, to) {
  mkdirSync(dirname(to), { recursive: true });
  try {
    ctx.require('node:fs').symlinkSync(from, to, 'junction');
  } catch {
    cpSync(from, to, { recursive: true, dereference: true });
  }
}

async function ensurePlaywrightBrowser(ctx, deckDir) {
  const cli = join(deckDir, 'node_modules', 'playwright-core', 'cli.js');
  if (!existsSync(cli)) { ctx.log('playwright-core not found; skipping browser check'); return false; }
  // No-op when the revision expected by this playwright-core is already present in PLAYWRIGHT_BROWSERS_PATH.
  const r = await ctx.run(process.execPath, [cli, 'install', 'chromium'], { cwd: deckDir, allowFail: true, timeoutMs: Math.min(ctx.remainingMs(), 6 * 60 * 1000) });
  return r.code === 0;
}

/**
 * Speaker notes (for the phone remote) and deck-health findings, produced by notes.mjs running as the deck user with
 * the deck's own @slidev/parser. Notes are a separate artifact served only to presenters; the site itself may be the
 * notes-free variant for everyone else.
 */
async function extractNotesAndLint(ctx, deckDir, outDir, result) {
  const { here, entry, deckPath, workRoot, log, run, remainingMs, mkdirForDeck, addAnnotation } = ctx;
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

function resolveSlidevBin(ctx, deckDir) {
  const candidates = [
    join(deckDir, 'node_modules', '@slidev', 'cli', 'bin', 'slidev.mjs'),
    join(ctx.here, 'node_modules', '@slidev', 'cli', 'bin', 'slidev.mjs'),
  ];
  for (const c of candidates) if (existsSync(c)) return c;
  throw new Error('Slidev CLI not found (deck has no @slidev/cli and the builder has no bundled copy)');
}

function injectPodiumMeta(ctx, indexHtml, addonProtocol = ADDON_PROTOCOL) {
  const html = readFileSync(indexHtml, 'utf8');
  if (html.includes('name="podium-build"')) return;
  writeFileSync(indexHtml, html.replace('<head>', `<head>${podiumMetaTags(ctx.buildId, ctx.slug, addonProtocol)}`));
}

/**
 * Adds the Podium sync addon (cross-device presenter sync) to the deck's headmatter. Only the headmatter block is
 * re-serialised; the slide content is left byte-for-byte intact. Works on the ephemeral checkout only.
 */
function injectPodiumAddon(ctx, deckDir, entryPath) {
  const { here, log } = ctx;
  const source = join(here, 'addon');
  if (!existsSync(join(source, 'setup', 'root.ts'))) { log('Podium addon not bundled with this builder; skipping sync addon'); return false; }
  const target = join(deckDir, '.podium-addon');
  rmSync(target, { recursive: true, force: true });
  cpSync(source, target, { recursive: true });

  const yaml = ctx.require('js-yaml');
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
