// Thumbnail capture, run as the unprivileged deck user (never as the orchestrator): serves the built site on a loopback
// port and screenshots the first slide. Arguments: <siteDir> <basePath> <kind> <outputJpeg>
import { createServer } from 'node:http';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { join, extname } from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const [site, basePath, kind, target] = process.argv.slice(2);
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.gif': 'image/gif', '.webp': 'image/webp', '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2', '.ttf': 'font/ttf', '.wasm': 'application/wasm', '.pdf': 'application/pdf' };

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
  const { chromium } = require('playwright-chromium');
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 720 }, colorScheme: 'dark' });
    const url = `http://127.0.0.1:${port}${basePath}${kind === 'slidev' ? '1' : ''}`;
    await page.goto(url, { waitUntil: 'networkidle', timeout: 45000 });
    if (kind === 'slidev') await page.waitForSelector('[data-slidev-no="1"]', { timeout: 20000 }).catch(() => {});
    await page.waitForTimeout(1200); // fonts, transitions
    await page.screenshot({ path: target, type: 'jpeg', quality: 80 });
    console.log('Thumbnail captured');
  } finally { await browser.close(); }
} finally { server.close(); }
