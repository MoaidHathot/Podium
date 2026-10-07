// Image references in a markdown deck that would break the build on a case-sensitive file system, or that point at
// nothing at all. Decks are usually written on Windows or macOS, where `slide1.png` happily opens `Slide1.PNG`;
// the builder runs on Linux. The repair copies the real file to the referenced name inside the throwaway workspace
// (the repository is never touched) and reports a notice; references with no file behind them get a placeholder
// image (so the deck still builds) and a warning. Both land in the deck's health annotations.
import { existsSync, readdirSync, statSync, mkdirSync, copyFileSync } from 'node:fs';
import { dirname, join, resolve, relative, sep } from 'node:path';

const IMAGE_REF = /!\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g;

/** Local image references in a markdown text with their 1-based line numbers. URLs and data URIs are skipped. */
export function findImageRefs(markdown) {
  const refs = [];
  String(markdown || '').split('\n').forEach((text, i) => {
    IMAGE_REF.lastIndex = 0;
    let m;
    while ((m = IMAGE_REF.exec(text))) {
      const ref = m[1];
      if (/^(https?:|data:|blob:|mailto:|#)/i.test(ref)) continue;
      refs.push({ ref, line: i + 1 });
    }
  });
  return refs;
}

/** The existing path that matches `relPath` under `baseDir` ignoring case, segment by segment, spelled as it is on disk; null when none does. */
export function resolveCaseInsensitive(baseDir, relPath) {
  let current = baseDir;
  for (const segment of relPath.split(/[\\/]+/).filter((s) => s && s !== '.')) {
    if (segment === '..') { current = dirname(current); continue; }
    if (!existsSync(current) || !statSync(current).isDirectory()) return null;
    const names = readdirSync(current);
    const match = names.find((name) => name === segment) ?? names.find((name) => name.toLowerCase() === segment.toLowerCase());
    if (!match) return null;
    current = join(current, match);
  }
  return current;
}

/**
 * Checks every local image the markdown file references. Returns the repairs made:
 *   { ref, line, kind: 'case', actual }   copied `actual` to the referenced path
 *   { ref, line, kind: 'missing', target } `makePlaceholder(target)` was asked to create the file
 * References that escape the deck folder are left alone (reported as 'outside').
 */
export function repairImageRefs(deckDir, markdownPath, markdown, makePlaceholder) {
  const repairs = [];
  const seen = new Set();
  for (const { ref, line } of findImageRefs(markdown)) {
    const clean = ref.split('#')[0].split('?')[0];
    if (!clean || seen.has(clean)) continue;
    seen.add(clean);
    const target = resolve(dirname(markdownPath), clean);
    if (relative(resolve(deckDir), target).startsWith('..') || relative(resolve(deckDir), target).startsWith(sep)) { repairs.push({ ref: clean, line, kind: 'outside' }); continue; }
    const actual = resolveCaseInsensitive(dirname(markdownPath), clean);
    if (actual && statSync(actual).isFile()) {
      if (resolve(actual) !== target) {
        // Spelled differently from the file on disk: fine on Windows/macOS, a missing file on Linux. The copy only
        // happens where the file system does not already resolve it (so the repair is a no-op on a Windows dev box).
        if (!existsSync(target)) { mkdirSync(dirname(target), { recursive: true }); copyFileSync(actual, target); }
        repairs.push({ ref: clean, line, kind: 'case', actual: relative(dirname(markdownPath), actual).replace(/\\/g, '/') });
      }
      continue;
    }
    if (/\.(png|jpe?g|gif|webp|bmp|svg|avif)$/i.test(clean)) {
      mkdirSync(dirname(target), { recursive: true });
      repairs.push({ ref: clean, line, kind: 'missing', target });
      if (makePlaceholder) makePlaceholder(target);
    }
  }
  return repairs;
}
