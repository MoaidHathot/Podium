// presenterm decks: image references repaired for a case-sensitive file system, then presenterm's HTML export (with
// the deck's config.yaml merged in) and PDF export (weasyprint), falling back to exports committed next to the source.
import { existsSync, readFileSync, writeFileSync, rmSync, copyFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { parsePresentermErrors } from '../lib/annotations.mjs';
import { repairImageRefs } from '../lib/images.mjs';
import { findSibling } from '../lib/shared.mjs';

export async function buildPresenterm(ctx, deckDir, outDir, result) {
  const { entry, exportPdf, run, remainingMs, mkdirForDeck } = ctx;
  const entryPath = join(deckDir, entry);
  if (!existsSync(entryPath)) throw new Error(`Entry ${entry} not found`);
  await repairImages(ctx, deckDir, entryPath);
  const site = join(outDir, 'site');
  mkdirForDeck(site); // presenterm (deck user) writes here
  const html = join(site, 'index.html');
  // presenterm's HTML export still probes the terminal (capability query + terminal size) in 0.16. Without a TTY the
  // size lookup fails ("Inappropriate ioctl"), and with a bare pty the query blocks forever. Giving it explicit export
  // dimensions and a pinned image protocol sidesteps both. The deck's own config.yaml is merged in so the exported
  // theme matches what the author sees locally.
  const configPath = writePresentermConfig(ctx, deckDir);
  const r = await run('presenterm', ['--export-html', entry, '--output', html, '--config-file', configPath, '--image-protocol', 'ascii-blocks'],
    { cwd: deckDir, allowFail: true, envExtra: { TERM: 'xterm-256color', COLUMNS: '120', LINES: '30' }, timeoutMs: Math.min(remainingMs(), 5 * 60 * 1000) });
  const base = entry.replace(/\.md$/i, '');
  if (r.code === 0 && existsSync(html)) result.hasSite = true;
  else {
    annotatePresentermErrors(ctx, r.out || '');
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

/** presenterm reports "error at main.md:34:1: could not load image 'x.png'": turn that into a positioned finding. */
function annotatePresentermErrors(ctx, output) {
  for (const a of parsePresentermErrors(output, ctx.deckPath, ctx.entry)) ctx.addAnnotation(a.path, a.line, a.level, a.message);
}

/**
 * Images a markdown deck references but Linux cannot open: a reference spelled differently from the file (fine on
 * the author's Windows or macOS box) is satisfied by a copy under the referenced name; a reference with no file at
 * all gets a generated placeholder that says so on the slide. Either way the deck builds and the finding lands in
 * the deck's health annotations (and GitHub check-run annotations) with the file position.
 */
async function repairImages(ctx, deckDir, markdownPath) {
  const { workRoot, log, addAnnotation } = ctx;
  const markdown = readFileSync(markdownPath, 'utf8');
  const placeholders = [];
  const repairs = repairImageRefs(deckDir, markdownPath, markdown, (target) => placeholders.push(target));
  const file = relative(join(workRoot, 'repo'), markdownPath).replace(/\\/g, '/');
  for (const r of repairs) {
    if (r.kind === 'case') { log(`Image ${r.ref} is ${r.actual} on disk; made available under the referenced name`); addAnnotation(file, r.line, 'notice', `Image path differs from the file in case (${r.actual}); Linux is case-sensitive, rename one of them`); }
    else if (r.kind === 'missing') { log(`Image ${r.ref} not found; placeholder used`); addAnnotation(file, r.line, 'warning', `Image not found: ${r.ref} (a placeholder was shown instead)`); }
    else if (r.kind === 'outside') addAnnotation(file, r.line, 'warning', `Image reference leaves the deck folder: ${r.ref}`);
  }
  if (placeholders.length === 0) return;
  // Rendered with the bundled Chromium (also used for thumbnails) so the message is legible on the slide itself.
  try {
    const { chromium } = ctx.require('playwright-chromium');
    const browser = await chromium.launch();
    try {
      const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
      for (const target of placeholders) {
        const label = relative(deckDir, target).replace(/\\/g, '/');
        const safe = label.replace(/&/g, '&amp;').replace(/</g, '&lt;');
        await page.setContent(`<!doctype html><html><body style="margin:0;background:#1f2430;color:#e6e9f0;font-family:system-ui,sans-serif">
          <div style="box-sizing:border-box;width:1280px;height:720px;padding:48px;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:24px;border:12px dashed #7c9cff">
            <div style="font-size:120px;line-height:1">&#9633;</div><div style="font-size:56px;font-weight:700">Image not found</div>
            <div style="font-size:30px;font-family:ui-monospace,Consolas,monospace;color:#9aa3b5;word-break:break-all;text-align:center">${safe}</div>
            <div style="font-size:22px;color:#6b7385">Placeholder generated by Podium; the file is missing from the repository</div>
          </div></body></html>`);
        const png = await page.screenshot({ type: 'png' });
        writeFileSync(target, png);
      }
    } finally { await browser.close(); }
  } catch (e) {
    // No browser: a 1x1 PNG keeps the export going (the annotation still names the missing file).
    log(`Placeholder rendering failed (${e.message}); using a blank image`);
    const blank = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=', 'base64');
    for (const target of placeholders) writeFileSync(target, blank);
  }
}

function writePresentermConfig(ctx, deckDir) {
  const { workRoot, log } = ctx;
  const yaml = ctx.require('js-yaml');
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
