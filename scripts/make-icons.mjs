// Renders the Podium app icons (PNG) from the brand mark so they stay in sync with favicon.svg.
// Run: node scripts/make-icons.mjs   (uses the builder's playwright-chromium). Commit the output.
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { mkdirSync, writeFileSync } from 'node:fs';

const here = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(here, '../builder/package.json'));
const { chromium } = require('playwright-chromium');
const out = resolve(here, '../src/Podium.Web/wwwroot/icons');
mkdirSync(out, { recursive: true });

const mark = '<path d="M4 5.5A1.5 1.5 0 0 1 5.5 4h13A1.5 1.5 0 0 1 20 5.5v9a1.5 1.5 0 0 1-1.5 1.5h-5v2h3a1 1 0 1 1 0 2h-9a1 1 0 1 1 0-2h3v-2h-5A1.5 1.5 0 0 1 4 14.5v-9Z" fill="#7c9cff"/>';
// "any" icons: rounded dark tile. "maskable": full-bleed background with the mark inside the 80% safe zone.
const svg = (size, maskable) => `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 24 24">
  <rect width="24" height="24" rx="${maskable ? 0 : 6}" fill="#131720"/>
  <g transform="translate(12 12) scale(${maskable ? 0.72 : 0.92}) translate(-12 -12)">${mark}</g>
</svg>`;

const browser = await chromium.launch();
const page = await browser.newPage({ deviceScaleFactor: 1 });
for (const [name, size, maskable] of [['icon-192.png', 192, false], ['icon-512.png', 512, false], ['maskable-512.png', 512, true], ['apple-touch-icon.png', 180, true]]) {
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(`<!doctype html><html><body style="margin:0;background:transparent">${svg(size, maskable)}</body></html>`);
  const png = await page.screenshot({ type: 'png', omitBackground: !maskable, clip: { x: 0, y: 0, width: size, height: size } });
  writeFileSync(resolve(out, name), png);
  console.log(`${name} ${png.length} bytes`);
}
await browser.close();
