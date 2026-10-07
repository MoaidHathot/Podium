#!/usr/bin/env node
// Per-kind builder fingerprints: which builder files produce a deck of each kind, hashed. The web app records the
// fingerprint of a deck's kind on every build and rebuilds a deck when it changes, so a change to the Slidev addon
// rebuilds Slidev decks only, and a change to the LibreOffice path rebuilds PowerPoint decks only. The image digest
// no longer matters: everything that goes into the image is listed here.
//   node fingerprint.mjs            prints one line per kind
//   node fingerprint.mjs --json     {"slidev":"...","presenterm":"...",...}
//   node fingerprint.mjs --env      slidev=...,presenterm=...   (what deploy.yml puts on the job)
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

/** Files every kind depends on (orchestrator, shared helpers, the image recipe and its dependencies). */
export const COMMON = ['build.mjs', 'lib/shared.mjs', 'lib/annotations.mjs', 'sheet.mjs', 'package.json', 'package-lock.json', 'Dockerfile', 'entrypoint.sh', 'fingerprint.mjs'];

/** Kind-specific files; a trailing slash means the whole directory. */
export const KINDS = {
  slidev: ['kinds/slidev.mjs', 'notes.mjs', 'thumb.mjs', 'addon/'],
  presenterm: ['kinds/presenterm.mjs', 'lib/images.mjs', 'thumb.mjs'],
  static: ['kinds/static.mjs', 'thumb.mjs'],
  powerpoint: ['kinds/files.mjs', 'lib/docdates.mjs', 'lib/zip.mjs'],
  pdf: ['kinds/files.mjs', 'lib/docdates.mjs', 'lib/zip.mjs'],
};

function* expand(root, entry) {
  const p = join(root, entry);
  if (entry.endsWith('/')) {
    if (!existsSync(p)) return;
    for (const name of readdirSync(p).sort()) {
      const child = join(p, name);
      if (statSync(child).isDirectory()) yield* expand(root, `${entry}${name}/`);
      else yield relative(root, child).replace(/\\/g, '/');
    }
  } else if (existsSync(p)) yield entry;
}

/** Fingerprint per kind for the builder at `root`: 16 hex characters of the hash over the kind's files (path and content). */
export function fingerprints(root = here) {
  const out = {};
  for (const [kind, files] of Object.entries(KINDS)) {
    const list = [...new Set([...COMMON, ...files].flatMap((e) => [...expand(root, e)]))].sort();
    const hash = createHash('sha256');
    for (const rel of list) {
      hash.update(rel); hash.update('\0');
      hash.update(readFileSync(join(root, rel)).toString('utf8').replace(/\r\n/g, '\n')); hash.update('\0');
    }
    out[kind] = hash.digest('hex').slice(0, 16);
  }
  return out;
}

export function toEnv(fps) { return Object.entries(fps).map(([k, v]) => `${k}=${v}`).join(','); }

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const fps = fingerprints();
  if (process.argv.includes('--json')) console.log(JSON.stringify(fps));
  else if (process.argv.includes('--env')) console.log(toEnv(fps));
  else for (const [k, v] of Object.entries(fps)) console.log(`${k.padEnd(11)} ${v}`);
}
