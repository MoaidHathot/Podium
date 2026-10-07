// Committed HTML exports: copied as they are (minus node_modules and dot files), with an optional PDF next to them.
import { existsSync, mkdirSync, readdirSync, cpSync, copyFileSync } from 'node:fs';
import { join, basename } from 'node:path';
import { findSibling } from '../lib/shared.mjs';

export async function buildStatic(ctx, deckDir, outDir, result) {
  const { entry, exportPdf } = ctx;
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
