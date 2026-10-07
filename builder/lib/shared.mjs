// Helpers every deck kind uses. Part of the common fingerprint: a change here rebuilds decks of every kind.
import { existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

// Protocol version of the bundled sync addon / bridge. Podium's live-ui.js stays inert on older builds, whose addon
// still draws its own pill, so a deck is never decorated twice during the rebuild wave after an upgrade.
export const ADDON_PROTOCOL = 3.2;

/** Locates an executable on PATH (with the Windows extensions in development). */
export function which(cmd) {
  const dirs = (process.env.PATH || '').split(process.platform === 'win32' ? ';' : ':');
  const exts = process.platform === 'win32' ? ['.exe', '.cmd', '.bat', ''] : [''];
  for (const d of dirs) for (const e of exts) { const p = join(d, cmd + e); if (d && existsSync(p)) return p; }
  return null;
}

/** `<base><ext>` in the directory, else the first file with that extension (an export the author committed). */
export function findSibling(dir, base, ext) {
  const exact = join(dir, base + ext);
  if (existsSync(exact)) return exact;
  const any = readdirSync(dir).filter((f) => f.toLowerCase().endsWith(ext)).sort();
  return any.length ? join(dir, any[0]) : null;
}

export function escapeHtml(s) { return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

/** The meta tags the web app reads from a served index.html: which build, which deck, which sync protocol it speaks. */
export function podiumMetaTags(buildId, slug, addonProtocol) {
  return `<meta name="podium-build" content="${buildId}"><meta name="podium-slug" content="${slug}"><meta name="podium-addon" content="${addonProtocol}">`;
}
