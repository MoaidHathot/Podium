// Slide-sheet composer, run as the unprivileged deck user: tiles the per-page JPEGs made by pdftoppm into one grid
// image using a headless page's canvas (no ImageMagick/ffmpeg in the builder image). Arguments: <tileDir> <cols> <outputJpeg>
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const [dir, colsArg, target] = process.argv.slice(2);
const cols = Math.max(1, Number(colsArg) || 6);
const tiles = readdirSync(dir).filter((f) => f.endsWith('.jpg')).sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
if (!tiles.length) { console.error('no tiles'); process.exit(1); }

const { chromium } = require('playwright-chromium');
const browser = await chromium.launch();
try {
  const page = await browser.newPage();
  await page.setContent('<!doctype html><html><body></body></html>');
  const dataUrls = tiles.map((t) => `data:image/jpeg;base64,${readFileSync(join(dir, t)).toString('base64')}`);
  const base64 = await page.evaluate(async ({ urls, cols }) => {
    const imgs = await Promise.all(urls.map((u) => new Promise((resolve, reject) => { const i = new Image(); i.onload = () => resolve(i); i.onerror = reject; i.src = u; })));
    const w = Math.max(...imgs.map((i) => i.naturalWidth));
    const h = Math.max(...imgs.map((i) => i.naturalHeight));
    const rows = Math.ceil(imgs.length / cols);
    const canvas = document.createElement('canvas');
    canvas.width = w * cols; canvas.height = h * rows;
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#000'; ctx.fillRect(0, 0, canvas.width, canvas.height);
    imgs.forEach((img, n) => ctx.drawImage(img, (n % cols) * w, Math.floor(n / cols) * h, w, h));
    return canvas.toDataURL('image/jpeg', 0.72).split(',')[1];
  }, { urls: dataUrls, cols });
  writeFileSync(target, Buffer.from(base64, 'base64'));
  console.log(`Slide sheet: ${tiles.length} slides, ${cols} per row`);
} finally { await browser.close(); }
