// PowerPoint and PDF decks: the file as it is (plus a PDF rendition, committed or converted with LibreOffice), the
// document's own dates, and the viewer site whose pages are rendered for Podium's pages viewer.
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync, readdirSync, copyFileSync, renameSync } from 'node:fs';
import { join, basename } from 'node:path';
import { officeDates, pdfInfoDates } from '../lib/docdates.mjs';
import { ADDON_PROTOCOL, which, escapeHtml, podiumMetaTags } from '../lib/shared.mjs';

export async function buildPowerPoint(ctx, deckDir, outDir, result) {
  const { entry, workRoot, log, run, remainingMs, mkdirForDeck } = ctx;
  const src = join(deckDir, entry);
  if (!existsSync(src)) throw new Error(`Entry ${entry} not found`);
  copyFileSync(src, join(outDir, 'deck.pptx'));
  result.hasPptx = true;
  recordDocumentDates(ctx, result, officeDates(readFileSync(src)), 'the presentation');

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
  await writeViewerSite(ctx, outDir, result, basename(entry));
}

export async function buildPdfDeck(ctx, deckDir, outDir, result) {
  const { entry, run } = ctx;
  const src = join(deckDir, entry);
  if (!existsSync(src)) throw new Error(`Entry ${entry} not found`);
  copyFileSync(src, join(outDir, 'deck.pdf'));
  result.hasPdf = true;
  const pdfinfo = which('pdfinfo');
  if (pdfinfo) {
    const r = await run(pdfinfo, ['-isodates', src], { cwd: deckDir, allowFail: true, echo: false, timeoutMs: 30000 });
    if (r.code === 0) recordDocumentDates(ctx, result, pdfInfoDates(r.out || ''), 'the PDF');
  }
  await writeViewerSite(ctx, outDir, result, basename(entry));
}

/** The document's own created/last-saved dates (when plausible) become the deck's authored dates on the server. */
function recordDocumentDates(ctx, result, dates, what) {
  if (dates.modified) result.authoredAt = dates.modified;
  if (dates.created) result.documentCreatedAt = dates.created;
  if (dates.modified || dates.created) ctx.log(`Dates from ${what}: created ${dates.created || '-'}, last saved ${dates.modified || '-'}`);
}

/**
 * The viewer site for PowerPoint/PDF decks. Pages are rendered to JPEGs (pdftoppm) and shown by Podium's own viewer
 * (/_podium/pages.js), which gives these decks what Slidev decks have: keyboard/touch navigation, deep links to a
 * page, cross-device sync, the phone remote, blackout, offline cache and the live session UI. Without pdftoppm (or
 * without a PDF) the browser's PDF viewer is used in an iframe, as before.
 */
async function writeViewerSite(ctx, outDir, result, fileName) {
  const { env, buildId, slug } = ctx;
  const site = join(outDir, 'site');
  mkdirSync(site, { recursive: true });
  const pdf = join(outDir, 'deck.pdf');
  const hasPdf = existsSync(pdf);
  if (hasPdf) copyFileSync(pdf, join(site, 'deck.pdf'));
  const title = escapeHtml(env.PODIUM_DECK_TITLE || fileName);
  const pages = hasPdf ? await renderPages(ctx, pdf, join(site, 'pages')) : null;
  const metas = podiumMetaTags(buildId, slug, pages ? ADDON_PROTOCOL : 0);
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

/** Renders every PDF page to <dir>/001.jpg ... at projector resolution. Returns {count,aspect} or null. */
async function renderPages(ctx, pdf, dir) {
  const { log, run, remainingMs, mkdirForDeck, SHEET_MAX_PAGES } = ctx;
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
