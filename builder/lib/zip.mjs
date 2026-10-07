// Minimal ZIP reader: pull one entry out of an in-memory archive (PPTX/DOCX are ZIP containers). Supports the
// stored and deflate methods, which is all Office writes. No ZIP64 (Office files stay far below 4 GB).
import { inflateRawSync } from 'node:zlib';

const EOCD = 0x06054b50, CENTRAL = 0x02014b50, LOCAL = 0x04034b50;

/** Lists entry names in the archive (central directory order). */
export function listZipEntries(buf) {
  return [...entries(buf)].map((e) => e.name);
}

/** Returns the decompressed bytes of `name` (exact match), or null when the archive has no such entry. */
export function readZipEntry(buf, name) {
  for (const e of entries(buf)) {
    if (e.name !== name) continue;
    if (e.localOffset + 30 > buf.length || buf.readUInt32LE(e.localOffset) !== LOCAL) throw new Error('zip: bad local header');
    const nameLen = buf.readUInt16LE(e.localOffset + 26);
    const extraLen = buf.readUInt16LE(e.localOffset + 28);
    const start = e.localOffset + 30 + nameLen + extraLen;
    const data = buf.subarray(start, start + e.compressedSize);
    if (e.method === 0) return Buffer.from(data);
    if (e.method === 8) return inflateRawSync(data);
    throw new Error(`zip: unsupported compression method ${e.method}`);
  }
  return null;
}

function* entries(buf) {
  if (!Buffer.isBuffer(buf) || buf.length < 22) return;
  // The end-of-central-directory record sits in the last 22 bytes plus an optional comment of up to 64 KB.
  const min = Math.max(0, buf.length - 22 - 65535);
  let eocd = -1;
  for (let i = buf.length - 22; i >= min; i--) if (buf.readUInt32LE(i) === EOCD) { eocd = i; break; }
  if (eocd < 0) return;
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  for (let n = 0; n < count && p + 46 <= buf.length; n++) {
    if (buf.readUInt32LE(p) !== CENTRAL) return;
    const method = buf.readUInt16LE(p + 10);
    const compressedSize = buf.readUInt32LE(p + 20);
    const nameLen = buf.readUInt16LE(p + 28), extraLen = buf.readUInt16LE(p + 30), commentLen = buf.readUInt16LE(p + 32);
    const localOffset = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nameLen);
    yield { name, method, compressedSize, localOffset };
    p += 46 + nameLen + extraLen + commentLen;
  }
}
