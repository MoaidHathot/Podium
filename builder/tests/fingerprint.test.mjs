import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, cpSync, readFileSync, writeFileSync, rmSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { COMMON, KINDS, fingerprints, toEnv } from '../fingerprint.mjs';

const builder = join(dirname(fileURLToPath(import.meta.url)), '..');
const imports = (file) => [...readFileSync(join(builder, file), 'utf8').matchAll(/^import .* from '(\.\.?\/[^']+)';/gm)].map((m) => m[1]);
const normalize = (from, spec) => join(dirname(from), spec).replace(/\\/g, '/');
const covered = (rel, list) => list.some((e) => (e.endsWith('/') ? rel.startsWith(e) : rel === e));

test('every local import of the orchestrator and the kind modules is part of a fingerprint', () => {
  for (const spec of imports('build.mjs')) {
    const rel = normalize('build.mjs', spec);
    assert.ok(covered(rel, COMMON) || rel.startsWith('kinds/'), `build.mjs imports ${rel}, which is in no fingerprint`);
  }
  for (const file of readdirSync(join(builder, 'kinds'))) {
    const kind = file.replace(/\.mjs$/, '');
    const owners = Object.entries(KINDS).filter(([, files]) => files.includes(`kinds/${file}`)).map(([k]) => k);
    assert.ok(owners.length > 0, `kinds/${file} belongs to no kind`);
    for (const spec of imports(`kinds/${file}`)) {
      const rel = normalize(`kinds/${file}`, spec);
      for (const owner of owners) assert.ok(covered(rel, COMMON) || covered(rel, KINDS[owner]), `kinds/${file} imports ${rel}, missing from the ${owner} fingerprint (${kind})`);
    }
  }
  for (const [kind, files] of Object.entries(KINDS)) for (const f of files) if (!f.endsWith('/')) assert.ok(readdirSync(join(builder, dirname(f))).includes(f.split('/').pop()), `${kind}: ${f} does not exist`);
});

test('a change touches only the kinds that use the file', () => {
  const dir = mkdtempSync(join(tmpdir(), 'podium-fp-'));
  try {
    for (const name of ['build.mjs', 'sheet.mjs', 'thumb.mjs', 'notes.mjs', 'fingerprint.mjs', 'package.json', 'package-lock.json', 'Dockerfile', 'entrypoint.sh']) cpSync(join(builder, name), join(dir, name));
    for (const sub of ['lib', 'kinds', 'addon']) cpSync(join(builder, sub), join(dir, sub), { recursive: true });
    const base = fingerprints(dir);
    assert.deepEqual(Object.keys(base).sort(), ['pdf', 'powerpoint', 'presenterm', 'slidev', 'static']);
    assert.ok(Object.values(base).every((v) => /^[0-9a-f]{16}$/.test(v)));
    assert.equal(base.powerpoint, base.pdf); // same files
    assert.deepEqual(fingerprints(dir), base); // stable

    writeFileSync(join(dir, 'addon', 'setup', 'root.ts'), readFileSync(join(dir, 'addon', 'setup', 'root.ts'), 'utf8') + '\n// change');
    const afterAddon = fingerprints(dir);
    assert.notEqual(afterAddon.slidev, base.slidev);
    for (const k of ['presenterm', 'static', 'powerpoint', 'pdf']) assert.equal(afterAddon[k], base[k], `${k} must not change for an addon edit`);

    writeFileSync(join(dir, 'lib', 'images.mjs'), readFileSync(join(dir, 'lib', 'images.mjs'), 'utf8') + '\n// change');
    const afterImages = fingerprints(dir);
    assert.notEqual(afterImages.presenterm, afterAddon.presenterm);
    for (const k of ['slidev', 'static', 'powerpoint', 'pdf']) assert.equal(afterImages[k], afterAddon[k]);

    writeFileSync(join(dir, 'lib', 'shared.mjs'), readFileSync(join(dir, 'lib', 'shared.mjs'), 'utf8') + '\n// change');
    const afterShared = fingerprints(dir);
    for (const k of Object.keys(base)) assert.notEqual(afterShared[k], afterImages[k], `${k} must change for a shared edit`);

    // CRLF checkouts hash the same as LF ones.
    writeFileSync(join(dir, 'kinds', 'static.mjs'), readFileSync(join(dir, 'kinds', 'static.mjs'), 'utf8').replace(/\n/g, '\r\n'));
    assert.equal(fingerprints(dir).static, afterShared.static);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('env form round-trips', () => {
  const env = toEnv({ slidev: 'a1', pdf: 'b2' });
  assert.equal(env, 'slidev=a1,pdf=b2');
});
