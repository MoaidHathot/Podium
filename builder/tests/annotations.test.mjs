import { test } from 'node:test';
import assert from 'node:assert/strict';
import { normalizeAnnotation, parsePresentermErrors } from '../lib/annotations.mjs';

test('presenterm image error becomes a positioned failure', () => {
  // Captured from the production builder log (colour codes included).
  const out = "\u001b[mfailed to build presentation: error at main.md:34:1:\n\u001b[m   | \u001b[m\u001b[m^ could not load image 'ev2-release-demo/slide1.png': No such file or directory (os error 2)\u001b[m\n";
  const findings = parsePresentermErrors(out, 'Microsoft/casual/ai/sa7bi-hackathon-overview', 'main.md');
  assert.equal(findings.length, 1);
  assert.deepEqual(findings[0], {
    path: 'Microsoft/casual/ai/sa7bi-hackathon-overview/main.md',
    line: 34,
    level: 'failure',
    message: "could not load image 'ev2-release-demo/slide1.png': No such file or directory (os error 2)",
  });
});

test('production log shape with source echo and timestamps yields the caret message', () => {
  const out = [
    '[2026-10-04T22:57:58.849Z]   \u001b[mfailed to build presentation: error at main.md:34:1:',
    '[2026-10-04T22:57:58.849Z]   \u001b[m34 | \u001b[m![](ev2-release-demo/slide1.png)',
    "[2026-10-04T22:57:58.849Z]   \u001b[m   | \u001b[m\u001b[m^ could not load image 'ev2-release-demo/slide1.png': No such file or directory (os error 2)\u001b[m",
  ].join('\n');
  const f = parsePresentermErrors(out, 'Microsoft/casual/ai/sa7bi-hackathon-overview', 'main.md');
  assert.equal(f.length, 1);
  assert.equal(f[0].line, 34);
  assert.equal(f[0].message, "could not load image 'ev2-release-demo/slide1.png': No such file or directory (os error 2)");
});

test('duplicate errors collapse and unrelated output yields nothing', () => {
  const out = 'error at main.md:3:1:\n | ^ boom\nerror at main.md:3:1:\n | ^ boom\n';
  assert.equal(parsePresentermErrors(out, '', 'main.md').length, 1);
  assert.deepEqual(parsePresentermErrors('all good', 'x', 'main.md'), []);
});

test('annotations from deck output are normalised and bounded', () => {
  assert.equal(normalizeAnnotation(null), null);
  assert.equal(normalizeAnnotation({ path: 'a', line: 1 }), null);
  const a = normalizeAnnotation({ path: 'p'.repeat(1000), line: -4, level: 'critical', message: 'm'.repeat(2000) });
  assert.equal(a.path.length, 300);
  assert.equal(a.line, 1);
  assert.equal(a.level, 'warning');
  assert.equal(a.message.length, 500);
});
