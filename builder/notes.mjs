// Runs as the deck user (deck markdown is untrusted input to the parser). Produces, for a Slidev deck:
//   notes.json : [{ index, title, note }]            speaker notes per page (hidden/disabled slides are skipped by the
//                                                     parser so indexes match what the browser shows)
//   lint       : [{ path, line, level, message }]     deck-health findings with repository-relative positions
// Usage: node notes.mjs <deckDir> <entry> <repoDir> <deckPathInRepo> <outNotesJson> <outLintJson>
import { createRequire } from 'node:module';
import { existsSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';

const [deckDir, entry, repoDir, deckPath, outNotes, outLint] = process.argv.slice(2);
const require = createRequire(join(deckDir, 'package.json'));
const { load } = require('@slidev/parser/fs');

const MAX_ASSET_BYTES = 5 * 1024 * 1024;
const findings = [];
const seenFindings = new Set();
function finding(filepath, line, level, message) {
  const rel = toRepoPath(filepath);
  const key = `${rel}:${line}:${message}`;
  if (seenFindings.has(key) || findings.length >= 50) return;
  seenFindings.add(key);
  findings.push({ path: rel, line: Math.max(1, line | 0), level, message });
}
function toRepoPath(p) {
  const abs = resolve(p);
  const rel = relative(resolve(repoDir), abs).split(sep).join('/');
  return rel.startsWith('..') ? (deckPath ? `${deckPath}/${entry}` : entry) : rel;
}

const data = await load({ userRoot: deckDir, roots: [deckDir], allowedRoots: [deckDir] }, join(deckDir, entry));

// Parser errors (missing/circular imports, unparseable frontmatter) are findings in their own right.
for (const [path, md] of Object.entries(data.markdownFiles)) {
  for (const e of md.errors || []) finding(path, (e.row | 0) + 1, 'failure', String(e.message).slice(0, 400));
}

const notes = data.slides.map((s) => ({
  index: s.index + 1,
  title: s.title || null,
  note: typeof s.note === 'string' && s.note.trim() ? s.note.trim() : null,
}));

// Asset references: markdown images, HTML img/video/source src, background/image frontmatter. Only local, relative
// references are checked; URLs and absolute `/public` paths (Slidev maps those to public/) are resolved against
// public/ when possible.
const refPatterns = [
  /!\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g,              // ![alt](path)
  /<(?:img|video|audio|source|iframe)\b[^>]*\bsrc\s*=\s*["']([^"']+)["']/gi, // <img src="">
  /\b(?:src|poster)\s*=\s*["']([^"']+)["']/gi,                        // component props
];
function checkAsset(ref, filepath, line) {
  if (!ref || /^(https?:|data:|blob:|mailto:|#|\$|\{)/i.test(ref) || ref.includes('${') || ref.startsWith('@')) return;
  const clean = ref.split('#')[0].split('?')[0];
  if (!clean) return;
  const candidates = clean.startsWith('/')
    ? [join(deckDir, 'public', clean), join(deckDir, clean)]
    : [join(dirname(filepath), clean), join(deckDir, clean), join(deckDir, 'public', clean)];
  const inside = (p) => !relative(resolve(deckDir), resolve(p)).startsWith('..');
  const hit = candidates.find((c) => inside(c) && existsSync(c) && statSync(c).isFile());
  if (!hit) {
    if (/\.(png|jpe?g|gif|svg|webp|avif|mp4|webm|mp3|wav|pdf|ico)$/i.test(clean))
      finding(filepath, line, 'warning', `Referenced file not found: ${clean}`);
    return;
  }
  const size = statSync(hit).size;
  if (size > MAX_ASSET_BYTES) finding(filepath, line, 'notice', `${clean} is ${(size / 1024 / 1024).toFixed(1)} MB; large assets slow the first load`);
}

for (const s of data.slides) {
  const filepath = s.source.filepath;
  const startLine = (s.source.start | 0) + 1;
  const lines = String(s.content || '').split('\n');
  lines.forEach((text, i) => {
    for (const re of refPatterns) {
      re.lastIndex = 0;
      let m;
      while ((m = re.exec(text))) checkAsset(m[1], filepath, startLine + i);
    }
  });
  const fm = s.frontmatter || {};
  for (const key of ['image', 'background']) {
    if (typeof fm[key] === 'string') checkAsset(fm[key], filepath, startLine);
  }
}

writeFileSync(outNotes, JSON.stringify(notes));
writeFileSync(outLint, JSON.stringify(findings));
process.stdout.write(`notes: ${notes.filter((n) => n.note).length}/${notes.length} slides have notes; lint: ${findings.length} finding(s)\n`);
