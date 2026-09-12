// Browser-side file upload and download for the IDE. Upload reads a File into a
// file-map entry (binary → base64, text → literal); download turns a map entry
// back into a real file; the whole project downloads as a store-only .zip that
// is binary-safe. DOM-dependent — the preview worker uses ./binary instead.

import { base64ToBytes, bytesToBase64, isBinaryPath, mimeOf } from './binary';

/** Reject uploads larger than this — a base64 asset inflates the site definition ~33%. */
export const MAX_UPLOAD_BYTES = 5 * 1024 * 1024;

export interface UploadResult {
  /** Raw path as offered by the browser (folder uploads carry a relative path); the vfs normalizes it. */
  path: string;
  /** Base64 for binary paths, literal text otherwise. */
  content: string;
}

/** Read one picked/dropped File into a file-map entry. Throws on oversize files. */
export async function readUpload(file: File): Promise<UploadResult> {
  if (file.size > MAX_UPLOAD_BYTES) {
    throw new Error(
      `${file.name} is larger than ${Math.round(MAX_UPLOAD_BYTES / 1024 / 1024)} MB.`,
    );
  }
  // A directory upload (<input webkitdirectory> or a dropped folder) carries the
  // path under webkitRelativePath; a plain file upload only has its name.
  const rel = (file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name;
  if (isBinaryPath(rel)) {
    return { path: rel, content: bytesToBase64(new Uint8Array(await file.arrayBuffer())) };
  }
  return { path: rel, content: await file.text() };
}

/** Download a single file-map entry as a real file. */
export function downloadFile(path: string, content: string): void {
  const name = path.slice(path.lastIndexOf('/') + 1);
  const blob = isBinaryPath(path)
    ? new Blob([base64ToBytes(content)], { type: mimeOf(path) })
    : new Blob([content], { type: 'text/plain;charset=utf-8' });
  triggerDownload(blob, name);
}

/** Download the whole file map as a (store-only, uncompressed) .zip archive. */
export function downloadProjectZip(zipName: string, files: Record<string, string>): void {
  triggerDownload(buildZip(files), zipName);
}

function triggerDownload(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// --- Minimal store-only ZIP writer (no dependency; binary-safe) ---------------

/** UTF-8 encode into a fresh ArrayBuffer-backed array (so it satisfies BlobPart). */
function utf8(s: string): Uint8Array<ArrayBuffer> {
  const encoded = new TextEncoder().encode(s);
  const out = new Uint8Array(encoded.length);
  out.set(encoded);
  return out;
}

function bytesOf(path: string, content: string): Uint8Array<ArrayBuffer> {
  return isBinaryPath(path) ? base64ToBytes(content) : utf8(content);
}

function buildZip(files: Record<string, string>): Blob {
  const parts: BlobPart[] = [];
  const central: Uint8Array<ArrayBuffer>[] = [];
  let offset = 0;

  for (const [path, content] of Object.entries(files)) {
    const nameBytes = utf8(path);
    const data = bytesOf(path, content);
    const crc = crc32(data);

    const local = new DataView(new ArrayBuffer(30));
    local.setUint32(0, 0x04034b50, true); // local file header signature
    local.setUint16(4, 20, true); // version needed
    local.setUint16(6, 0x0800, true); // flags: bit 11 = UTF-8 filename
    local.setUint16(8, 0, true); // compression: 0 = store
    local.setUint16(10, 0, true); // mod time
    local.setUint16(12, 0, true); // mod date
    local.setUint32(14, crc, true);
    local.setUint32(18, data.length, true); // compressed size
    local.setUint32(22, data.length, true); // uncompressed size
    local.setUint16(26, nameBytes.length, true);
    local.setUint16(28, 0, true); // extra length
    parts.push(new Uint8Array(local.buffer), nameBytes, data);

    const cd = new DataView(new ArrayBuffer(46));
    cd.setUint32(0, 0x02014b50, true); // central directory header signature
    cd.setUint16(4, 20, true); // version made by
    cd.setUint16(6, 20, true); // version needed
    cd.setUint16(8, 0x0800, true); // flags
    cd.setUint16(10, 0, true); // compression
    cd.setUint16(12, 0, true); // mod time
    cd.setUint16(14, 0, true); // mod date
    cd.setUint32(16, crc, true);
    cd.setUint32(20, data.length, true); // compressed size
    cd.setUint32(24, data.length, true); // uncompressed size
    cd.setUint16(28, nameBytes.length, true);
    cd.setUint16(30, 0, true); // extra length
    cd.setUint16(32, 0, true); // comment length
    cd.setUint16(34, 0, true); // disk number start
    cd.setUint16(36, 0, true); // internal attributes
    cd.setUint32(38, 0, true); // external attributes
    cd.setUint32(42, offset, true); // local header offset
    const cdEntry = new Uint8Array(46 + nameBytes.length);
    cdEntry.set(new Uint8Array(cd.buffer), 0);
    cdEntry.set(nameBytes, 46);
    central.push(cdEntry);

    offset += 30 + nameBytes.length + data.length;
  }

  const centralSize = central.reduce((n, e) => n + e.length, 0);
  const eocd = new DataView(new ArrayBuffer(22));
  eocd.setUint32(0, 0x06054b50, true); // end of central directory signature
  eocd.setUint16(4, 0, true); // disk number
  eocd.setUint16(6, 0, true); // disk with central directory
  eocd.setUint16(8, central.length, true); // entries on this disk
  eocd.setUint16(10, central.length, true); // total entries
  eocd.setUint32(12, centralSize, true);
  eocd.setUint32(16, offset, true); // central directory offset
  eocd.setUint16(20, 0, true); // comment length

  return new Blob([...parts, ...central, new Uint8Array(eocd.buffer)], {
    type: 'application/zip',
  });
}

let CRC_TABLE: Uint32Array | null = null;

function crcTable(): Uint32Array {
  if (CRC_TABLE) return CRC_TABLE;
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c >>> 0;
  }
  CRC_TABLE = table;
  return table;
}

function crc32(bytes: Uint8Array): number {
  const table = crcTable();
  let c = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) {
    c = table[(c ^ bytes[i]) & 0xff] ^ (c >>> 8);
  }
  return (c ^ 0xffffffff) >>> 0;
}
