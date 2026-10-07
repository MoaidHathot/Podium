import { test } from 'node:test';
import assert from 'node:assert/strict';
import { deflateRawSync } from 'node:zlib';
import { mkdtempSync, writeFileSync, mkdirSync, existsSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { readZipEntry, listZipEntries } from '../lib/zip.mjs';
import { officeDates, pdfInfoDates, isoDate } from '../lib/docdates.mjs';
import { findImageRefs, resolveCaseInsensitive, repairImageRefs } from '../lib/images.mjs';

// Builds a small ZIP in memory (stored or deflated entries) the way any archiver would lay it out.
function makeZip(files, deflate) {
  const parts = [], central = [];
  let offset = 0;
  for (const [name, content] of Object.entries(files)) {
    const raw = Buffer.from(content, 'utf8');
    const data = deflate ? deflateRawSync(raw) : raw;
    const nameBuf = Buffer.from(name, 'utf8');
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(deflate ? 8 : 0, 8);
    local.writeUInt32LE(data.length, 18); local.writeUInt32LE(raw.length, 22); local.writeUInt16LE(nameBuf.length, 26); local.writeUInt16LE(0, 28);
    const cd = Buffer.alloc(46);
    cd.writeUInt32LE(0x02014b50, 0); cd.writeUInt16LE(deflate ? 8 : 0, 10); cd.writeUInt32LE(data.length, 20); cd.writeUInt32LE(raw.length, 24);
    cd.writeUInt16LE(nameBuf.length, 28); cd.writeUInt32LE(offset, 42);
    central.push(Buffer.concat([cd, nameBuf]));
    parts.push(local, nameBuf, data);
    offset += local.length + nameBuf.length + data.length;
  }
  const cdBuf = Buffer.concat(central);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0); eocd.writeUInt16LE(central.length, 8); eocd.writeUInt16LE(central.length, 10);
  eocd.writeUInt32LE(cdBuf.length, 12); eocd.writeUInt32LE(offset, 16);
  return Buffer.concat([...parts, cdBuf, eocd, Buffer.from('a comment')]);
}

const core = `<?xml version="1.0"?><cp:coreProperties xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>x</dc:title><dcterms:created xsi:type="dcterms:W3CDTF">2017-01-29T10:12:00Z</dcterms:created><dcterms:modified xsi:type="dcterms:W3CDTF">2019-10-30T07:51:12Z</dcterms:modified></cp:coreProperties>`;

test('zip entries are listed and read for stored and deflated archives', () => {
  for (const deflate of [false, true]) {
    const zip = makeZip({ '[Content_Types].xml': '<Types/>', 'docProps/core.xml': core, 'ppt/slides/slide1.xml': '<p:sld/>' }, deflate);
    assert.deepEqual(listZipEntries(zip), ['[Content_Types].xml', 'docProps/core.xml', 'ppt/slides/slide1.xml']);
    assert.equal(readZipEntry(zip, 'docProps/core.xml').toString('utf8'), core);
    assert.equal(readZipEntry(zip, 'nope.xml'), null);
  }
  assert.deepEqual(listZipEntries(Buffer.from('not a zip at all, really')), []);
});

test('office dates come from docProps/core.xml and are normalised', () => {
  const zip = makeZip({ 'docProps/core.xml': core }, true);
  assert.deepEqual(officeDates(zip), { created: '2017-01-29T10:12:00.000Z', modified: '2019-10-30T07:51:12.000Z' });
  assert.deepEqual(officeDates(makeZip({ 'other.xml': '<x/>' }, false)), { created: null, modified: null });
  assert.deepEqual(officeDates(Buffer.from('garbage')), { created: null, modified: null });
});

test('pdfinfo dates parse in iso and native forms, implausible values are dropped', () => {
  const iso = 'Title:           Deck\nCreationDate:    2024-02-13T09:00:00Z\nModDate:         2024-02-13T10:30:00+02:00\nPages:           12\n';
  assert.deepEqual(pdfInfoDates(iso), { created: '2024-02-13T09:00:00.000Z', modified: '2024-02-13T08:30:00.000Z' });
  assert.equal(isoDate("D:20191030095100+02'00'"), '2019-10-30T07:51:00.000Z');
  assert.equal(isoDate('2024-12-05T08:31:24-05'), '2024-12-05T13:31:24.000Z'); // pdfinfo's hour-only offset
  assert.equal(isoDate('D:20191030'), '2019-10-30T00:00:00.000Z');
  assert.equal(isoDate('D:19800101000000Z'), null);           // before 1990: a template or a clock that was never set
  assert.equal(isoDate('2999-01-01T00:00:00Z'), null);         // in the future
  assert.equal(isoDate('yesterday'), null);
  assert.deepEqual(pdfInfoDates(''), { created: null, modified: null });
});

test('image references are found, case mismatches repaired and missing files given a placeholder', () => {
  const md = '# Slide\n![](sa7bi.png)\n\nText ![logo](Images/Logo.PNG "title") and ![](https://x.test/a.png)\n\n![](ev2/slide1.png)\n![](gone/missing.png)\n![](../outside.png)\n![](notes.txt)';
  assert.deepEqual(findImageRefs(md).map((r) => `${r.line}:${r.ref}`), ['2:sa7bi.png', '4:Images/Logo.PNG', '6:ev2/slide1.png', '7:gone/missing.png', '8:../outside.png', '9:notes.txt']);

  const dir = mkdtempSync(join(tmpdir(), 'podium-images-'));
  try {
    const deck = join(dir, 'deck');
    mkdirSync(join(deck, 'ev2'), { recursive: true }); mkdirSync(join(deck, 'images'));
    writeFileSync(join(deck, 'sa7bi.png'), 'png'); writeFileSync(join(deck, 'ev2', 'Slide1.PNG'), 'png1'); writeFileSync(join(deck, 'images', 'logo.png'), 'logo');
    writeFileSync(join(deck, 'main.md'), md);
    assert.equal(resolveCaseInsensitive(deck, 'EV2/slide1.PNG'), join(deck, 'ev2', 'Slide1.PNG'));
    assert.equal(resolveCaseInsensitive(deck, 'ev2/nothing.png'), null);

    const placeholders = [];
    const repairs = repairImageRefs(deck, join(deck, 'main.md'), md, (target) => { placeholders.push(target); writeFileSync(target, 'placeholder'); });
    assert.deepEqual(repairs.map((r) => [r.kind, r.ref, r.actual ?? null]), [
      ['case', 'Images/Logo.PNG', 'images/logo.png'],
      ['case', 'ev2/slide1.png', 'ev2/Slide1.PNG'],
      ['missing', 'gone/missing.png', null],
      ['outside', '../outside.png', null],
    ]);
    assert.equal(readFileSync(join(deck, 'ev2', 'slide1.png'), 'utf8'), 'png1');          // the referenced name now exists
    assert.equal(readFileSync(join(deck, 'Images', 'Logo.PNG'), 'utf8'), 'logo');
    assert.deepEqual(placeholders, [join(deck, 'gone', 'missing.png')]);
    assert.ok(existsSync(join(deck, 'gone', 'missing.png')));
    assert.ok(!existsSync(join(dir, 'outside.png')));                                        // nothing is written outside the deck
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
