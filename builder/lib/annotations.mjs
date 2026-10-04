// Pure helpers shared by the builder, kept free of process/env state so they can be unit-tested with `node --test`.

const LEVELS = new Set(['notice', 'warning', 'failure']);

/** Normalises a deck-health finding; returns null for garbage (deck-controlled input). */
export function normalizeAnnotation(a) {
  if (!a || typeof a !== 'object') return null;
  const message = String(a.message ?? '').trim();
  if (!message) return null;
  return {
    path: String(a.path ?? '').slice(0, 300),
    line: Math.max(1, Number(a.line) | 0),
    level: LEVELS.has(a.level) ? a.level : 'warning',
    message: message.slice(0, 500),
  };
}

/**
 * presenterm prints its failures like
 *   failed to build presentation: error at main.md:34:1:
 *      | ^ could not load image 'ev2-release-demo/slide1.png': No such file or directory (os error 2)
 * (ANSI colour codes interleaved). Turns them into positioned findings relative to the repository root.
 */
export function parsePresentermErrors(output, deckPath, entry) {
  const text = String(output || '').replace(/\x1b\[[0-9;]*[A-Za-z]/g, '');
  const findings = [];
  const seen = new Set();
  const push = (file, line, level, message) => {
    const key = `${file}:${line}:${message}`;
    if (seen.has(key)) return;
    seen.add(key);
    findings.push({ path: deckPath ? `${deckPath}/${file}` : file, line, level, message });
  };
  const re = /error at ([^\s:]+):(\d+):\d+:?\s*\n?(?:\s*\|?\s*\^?\s*)?([^\n]*)/g;
  let m;
  while ((m = re.exec(text))) {
    const detail = (m[3] || '').replace(/^\|?\s*\^\s*/, '').trim();
    push(m[1], Number(m[2]), 'failure', detail || 'presenterm could not render this slide');
  }
  if (!findings.length) {
    const img = /could not load image '([^']+)'/g;
    while ((m = img.exec(text))) push(entry, 1, 'warning', `Referenced image not found: ${m[1]}`);
  }
  return findings;
}
