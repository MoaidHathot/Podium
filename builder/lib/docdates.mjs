// The dates a presentation file carries about itself: when its author created it and last saved it. These are what
// "sort by date" should mean for a deck that was committed long after it was written (an archive import, a copy
// from OneDrive); git only knows when the file reached the repository.
import { readZipEntry } from './zip.mjs';

/** PowerPoint/Word/Excel (OOXML): docProps/core.xml carries dcterms:created and dcterms:modified. */
export function officeDates(zipBuffer) {
  let xml;
  try { xml = readZipEntry(zipBuffer, 'docProps/core.xml')?.toString('utf8'); } catch { return empty(); }
  if (!xml) return empty();
  const pick = (tag) => {
    const m = xml.match(new RegExp(`<dcterms:${tag}\\b[^>]*>([^<]+)</dcterms:${tag}>`));
    return m ? isoDate(m[1].trim()) : null;
  };
  return { created: pick('created'), modified: pick('modified') };
}

/** Output of `pdfinfo -isodates`: CreationDate / ModDate lines in ISO 8601 (or poppler's older local format). */
export function pdfInfoDates(text) {
  const pick = (label) => {
    const m = String(text || '').match(new RegExp(`^${label}:\\s*(.+)$`, 'm'));
    return m ? isoDate(m[1].trim()) : null;
  };
  return { created: pick('CreationDate'), modified: pick('ModDate') };
}

/** Normalises a date string to ISO 8601 UTC; null for anything unparseable or outside 1990..(now + 1 day). */
export function isoDate(value) {
  if (!value) return null;
  let s = String(value).trim();
  // PDF native form: D:YYYYMMDDHHmmSS+02'00'
  const pdf = s.match(/^D:(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?(?:([+-])(\d{2})'?(\d{2})?'?|Z)?$/);
  if (pdf) {
    const [, y, mo = '01', d = '01', h = '00', mi = '00', se = '00', sign, oh, om = '00'] = pdf;
    s = `${y}-${mo}-${d}T${h}:${mi}:${se}${sign ? `${sign}${oh}:${om}` : 'Z'}`;
  }
  const t = Date.parse(s);
  if (Number.isNaN(t)) return null;
  const date = new Date(t);
  if (date.getUTCFullYear() < 1990 || t > Date.now() + 86_400_000) return null;
  return date.toISOString();
}

function empty() { return { created: null, modified: null }; }
