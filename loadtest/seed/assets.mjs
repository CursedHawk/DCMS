// Synthetic fixture content, generated rather than committed.
//
// The media pipeline content-sniffs by magic bytes and re-encodes images through
// ImageSharp, so a fixture has to be a genuinely valid file — a renamed text blob is
// rejected at ingest and the media scenario would measure the rejection path. Generating
// them keeps real binaries out of the repo and lets a profile ask for any dimensions,
// which is what makes the webp ladder (320/640/1280/1920) interesting: an image smaller
// than a rung is skipped, so a 320px fixture and a 1920px one exercise different amounts
// of work.

import { deflateSync } from 'node:zlib';

const crcTable = Int32Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c;
});

function crc32(buf) {
  let c = -1;
  for (const byte of buf) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

function chunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

/**
 * A valid RGB PNG of the requested size: a smooth gradient with low-amplitude noise.
 *
 * The mix is the whole point, and getting it wrong distorts the media scenario in
 * opposite directions. A flat colour compresses to almost nothing, so a "1920px
 * fixture" arrives as a few kilobytes and the upload path never sees a realistic body.
 * Pure noise is incompressible, and a 1920px frame of it lands at 7.3 MB -- four VUs
 * uploading that measures the load generator's uplink, not the worker's queue.
 *
 * A gradient plus +/-16 of noise compresses roughly the way a photograph does, so file
 * size tracks dimensions the way the media pipeline will actually see. Deterministic,
 * so two runs upload identical bytes and a size difference means something changed.
 */
export function png(width, height, seed = 1) {
  const raw = Buffer.alloc((width * 3 + 1) * height);
  let state = seed >>> 0;
  let offset = 0;
  const row = new Uint8Array(width * 3);

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      for (let c = 0; c < 3; c++) {
        state = (state * 1664525 + 1013904223) >>> 0;
        const base = ((x * 255 / width) * (c === 0 ? 1 : 0.6)
                    + (y * 255 / height) * (c === 2 ? 1 : 0.4)) / 1.6;
        const noise = ((state >>> 24) & 15) - 8;
        row[x * 3 + c] = Math.max(0, Math.min(255, Math.round(base + noise)));
      }
    }
    // Filter type 1 (Sub): store each byte as its difference from the pixel to its
    // left. On a gradient those differences are near-constant, which is exactly what
    // deflate is good at -- with filter 0 the same image is incompressible and a
    // 1920px frame lands at 6.6 MB instead of ~1 MB.
    raw[offset++] = 1;
    for (let i = 0; i < row.length; i++) {
      raw[offset++] = (row[i] - (i >= 3 ? row[i - 3] : 0)) & 0xff;
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;    // bit depth
  ihdr[9] = 2;    // colour type: truecolour
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/**
 * The static-files (Mode C) bundle the site-host scenario serves.
 *
 * Shaped to make site-host's behaviour visible rather than to look like a website.
 * It carries, deliberately:
 *   - index.html, so the SPA-style fallback has something to fall back to
 *   - named pages, which are the "exact candidate" case in SiteHostEndpoints.CandidatesFor
 *   - an `events_@.html` wildcard page, which is the SECOND candidate — a request for
 *     /events/<anything> misses the exact file and costs a second MinIO round trip
 *   - assets across three size bands, because a handler that buffers whole objects into
 *     a byte[] behaves very differently at 2 KB and at 2 MB, and that difference is the
 *     entire hypothesis about this service
 */
export function siteBundle({ pages = 8, assetBytes = 200_000 } = {}) {
  const files = new Map();
  const shell = (title, body) => Buffer.from(
`<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>${title}</title>
<link rel="stylesheet" href="/style.css"></head>
<body><h1>${title}</h1>${body}<script src="/app.js"></script></body></html>
`, 'utf8');

  files.set('index.html', shell('Load test site',
    Array.from({ length: pages }, (_, i) => `<a href="/page-${i}">page ${i}</a>`).join('\n')));

  for (let i = 0; i < pages; i++) {
    files.set(`page-${i}.html`, shell(`Page ${i}`, `<p>${'content '.repeat(200)}</p>`));
  }

  // The wildcard detail page: /events/<slug> resolves here only after the exact
  // candidate misses, which is the two-round-trip path worth measuring.
  files.set('events_@.html', shell('Event', '<p data-event-detail>Event detail</p>'));

  files.set('style.css', Buffer.from(`body{font:16px system-ui;margin:2rem}${'/*pad*/'.repeat(500)}`, 'utf8'));
  files.set('app.js', Buffer.from(`console.log('loadtest');${'/*pad*/'.repeat(500)}`, 'utf8'));

  files.set('img/small.png', png(64, 64, 7));
  files.set('img/medium.png', png(400, 400, 11));
  files.set('img/large.png', png(1200, Math.max(200, Math.ceil(assetBytes / 3600)), 13));

  return files;
}
