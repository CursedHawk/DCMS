// Binary-asset handling for the Mode B file map. The map is text-only on the
// wire ({ files: { path: string } }), so binary files (images, fonts, …) are
// stored base64-encoded and identified purely by extension — the same rule the
// offline builder (ReactAppBuilder.IsBinaryPath) and the preview bundler apply,
// so a round-trip through save → build → preview is lossless.
//
// This module is DOM-free (atob/btoa/Uint8Array only) so the preview web worker
// can import it too. DOM-dependent upload/download lives in ./fileio.

/** Extensions whose file-map value is base64 of the raw bytes (everything else is literal text). */
export const BINARY_EXTENSIONS = new Set([
  // images
  'png',
  'jpg',
  'jpeg',
  'gif',
  'webp',
  'avif',
  'ico',
  'bmp',
  // fonts
  'woff',
  'woff2',
  'ttf',
  'otf',
  'eot',
  // media
  'mp3',
  'mp4',
  'webm',
  'ogg',
  'wav',
  // other
  'pdf',
]);

function extOf(path: string): string {
  const dot = path.lastIndexOf('.');
  return dot < 0 ? '' : path.slice(dot + 1).toLowerCase();
}

/** True when a path's file-map value is base64-encoded binary rather than text. */
export function isBinaryPath(path: string): boolean {
  return BINARY_EXTENSIONS.has(extOf(path));
}

/** Best-effort MIME type from a path's extension, for previews and downloads. */
export function mimeOf(path: string): string {
  switch (extOf(path)) {
    case 'png':
      return 'image/png';
    case 'jpg':
    case 'jpeg':
      return 'image/jpeg';
    case 'gif':
      return 'image/gif';
    case 'webp':
      return 'image/webp';
    case 'avif':
      return 'image/avif';
    case 'ico':
      return 'image/x-icon';
    case 'bmp':
      return 'image/bmp';
    case 'svg':
      return 'image/svg+xml';
    case 'woff':
      return 'font/woff';
    case 'woff2':
      return 'font/woff2';
    case 'ttf':
      return 'font/ttf';
    case 'otf':
      return 'font/otf';
    case 'eot':
      return 'application/vnd.ms-fontobject';
    case 'mp3':
      return 'audio/mpeg';
    case 'wav':
      return 'audio/wav';
    case 'ogg':
      return 'audio/ogg';
    case 'mp4':
      return 'video/mp4';
    case 'webm':
      return 'video/webm';
    case 'pdf':
      return 'application/pdf';
    default:
      return 'application/octet-stream';
  }
}

/** True for an image type we can render inline in the binary file viewer. */
export function isPreviewableImage(path: string): boolean {
  return mimeOf(path).startsWith('image/');
}

/** Encode raw bytes to a base64 string (chunked to stay under the arg-count limit). */
export function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}

/** Decode a base64 string back to raw bytes (ArrayBuffer-backed, so Blob-safe). */
export function base64ToBytes(b64: string): Uint8Array<ArrayBuffer> {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

/** A data: URI for a binary file-map entry (base64 value) — used by previews. */
export function dataUriFor(path: string, base64Value: string): string {
  return `data:${mimeOf(path)};base64,${base64Value}`;
}
