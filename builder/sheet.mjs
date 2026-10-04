// Slide-sheet composer, run as the unprivileged deck user (never as the orchestrator). Two modes:
//   node sheet.mjs --tiles <tileDir> <cols> <outputJpeg>
//       tiles the per-page JPEGs made by pdftoppm into one grid (presenterm / PowerPoint / PDF decks: pages = slides)
//   node sheet.mjs --site <siteDir> <basePath> <count> <cols> <outputJpeg>
//       serves the built Slidev site on a loopback port, screenshots every slide (initial click state) and tiles them;
//       used for Slidev because its PDF export may contain one page per click step, which would break slide numbering
// Prints the geometry as the last stdout line: {"count":n,"cols":c,"rows":r,"cellWidth":w,"cellHeight":h}
import { createServer } from 'node:http';
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, extname } from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright-chromium');
const args = process.argv.slice(2);
const mode = args[0];
const CELL_W = 320, CELL_H = 180;

async function tile(page, dataUrls, cols) {
  await page.setContent('<!doctype html><html><body></body></html>');
  return page.evaluate(async ({ urls, cols }) => {
    const imgs = await Promise.all(urls.map((u) => new Promise((resolve, reject) => { const i = new Image(); i.onload = () => resolve(i); i.onerror = reject; i.src = u; })));
    const w = Math.max(...imgs.map((i) => i.naturalWidth));
    const h = Math.max(...imgs.map((i) => i.naturalHeight));
    const rows = Math.ceil(imgs.length / cols);
    const canvas = document.createElement('canvas');
    canvas.width = w * cols; canvas.height = h * rows;
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#000'; ctx.fillRect(0, 0, canvas.width, canvas.height);
    imgs.forEach((img, n) => ctx.drawImage(img, (n % cols) * w, Math.floor(n / cols) * h, w, h));
    return { data: canvas.toDataURL('image/jpeg', 0.72).split(',')[1], w, h, rows };
  }, { urls: dataUrls, cols });
}

const browser = await chromium.launch();
try {
  if (mode === '--tiles') {
    const [, dir, colsArg, target] = args;
    const cols = Math.max(1, Number(colsArg) || 6);
    const tiles = readdirSync(dir).filter((f) => f.endsWith('.jpg')).sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
    if (!tiles.length) { console.error('no tiles'); process.exit(1); }
    const page = await browser.newPage();
    const urls = tiles.map((t) => `data:image/jpeg;base64,${readFileSync(join(dir, t)).toString('base64')}`);
    const r = await tile(page, urls, cols);
    writeFileSync(target, Buffer.from(r.data, 'base64'));
    console.log(JSON.stringify({ count: tiles.length, cols, rows: r.rows, cellWidth: r.w, cellHeight: r.h }));
  } else if (mode === '--site') {
    const [, site, basePath, countArg, colsArg, target] = args;
    const count = Math.min(400, Math.max(1, Number(countArg) || 1));
    const cols = Math.max(1, Number(colsArg) || 6);
    const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.gif': 'image/gif', '.webp': 'image/webp', '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2', '.ttf': 'font/ttf', '.wasm': 'application/wasm' };
    const server = createServer((req, res) => {
      let p = decodeURIComponent((req.url || '/').split('?')[0]);
      if (p.startsWith(basePath)) p = p.slice(basePath.length); else p = p.replace(/^\/+/, '');
      let file = join(site, p);
      if (!file.startsWith(site) || !existsSync(file) || statSync(file).isDirectory()) file = join(site, 'index.html');
      if (!existsSync(file)) { res.statusCode = 404; res.end(); return; }
      res.setHeader('content-type', types[extname(file).toLowerCase()] || 'application/octet-stream');
      res.end(readFileSync(file));
    });
    await new Promise((r) => server.listen(0, '127.0.0.1', r));
    const port = server.address().port;
    try {
      // Slidev scales the slide canvas to the viewport, so a small viewport yields a sharp small render directly.
      const page = await browser.newPage({ viewport: { width: CELL_W, height: CELL_H }, colorScheme: 'dark' });
      const shots = [];
      for (let n = 1; n <= count; n++) {
        await page.goto(`http://127.0.0.1:${port}${basePath}${n}`, { waitUntil: 'networkidle', timeout: 30000 }).catch(() => {});
        await page.waitForSelector(`[data-slidev-no="${n}"]`, { timeout: 8000 }).catch(() => {});
        await page.waitForTimeout(n === 1 ? 1000 : 250);
        shots.push(`data:image/jpeg;base64,${(await page.screenshot({ type: 'jpeg', quality: 70 })).toString('base64')}`);
      }
      const composer = await browser.newPage();
      const r = await tile(composer, shots, cols);
      writeFileSync(target, Buffer.from(r.data, 'base64'));
      console.log(JSON.stringify({ count, cols, rows: r.rows, cellWidth: r.w, cellHeight: r.h }));
    } finally { server.close(); }
  } else {
    console.error('usage: sheet.mjs --tiles <dir> <cols> <out> | --site <siteDir> <basePath> <count> <cols> <out>');
    process.exit(2);
  }
} finally { await browser.close(); }
