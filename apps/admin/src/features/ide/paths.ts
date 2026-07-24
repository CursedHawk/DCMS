// Small helpers shared across the IDE: path normalization (mirroring the
// backend's StaticSiteFiles.NormalizeEntryPath so a save is never rejected),
// language-id mapping, and the file:/// URI scheme Monaco models live under.

/** Files the platform toolchain owns — shown read-only in the tree. */
export const TOOLCHAIN_FILES = new Set(['package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml']);

/** Monaco language id for a file path, from its extension. */
export function languageOf(path: string): string {
  const ext = path.slice(path.lastIndexOf('.') + 1).toLowerCase();
  switch (ext) {
    case 'ts':
      return 'typescript';
    case 'tsx':
      return 'typescript'; // Monaco uses the same worker; scriptKind handled by .tsx uri
    case 'js':
    case 'mjs':
    case 'cjs':
    case 'jsx':
      return 'javascript';
    case 'json':
      return 'json';
    case 'css':
      return 'css';
    case 'scss':
      return 'scss';
    case 'less':
      return 'less';
    case 'html':
    case 'htm':
      return 'html';
    case 'md':
      return 'markdown';
    default:
      return 'plaintext';
  }
}

/** The file:/// URI a path maps to for its Monaco model. */
export function uriOf(path: string): string {
  return `file:///${path.replace(/^\/+/, '')}`;
}

/** Path back from a file:/// URI. */
export function pathOfUri(uri: string): string {
  return uri.replace(/^file:\/\/\//, '');
}

/**
 * Normalize a relative path the way the backend does (reject traversal, absolute
 * and drive-qualified paths, collapse `.`), returning null when it must be
 * rejected. Keep in lockstep with StaticSiteFiles.NormalizeEntryPath.
 */
export function normalizePath(raw: string): string | null {
  if (!raw || !raw.trim()) return null;
  const value = raw.replace(/\\/g, '/').trim().replace(/^\/+/, '');
  if (value.length === 0 || value.endsWith('/')) return null;
  const cleaned: string[] = [];
  for (const seg of value.split('/')) {
    if (seg === '' || seg === '.') continue;
    if (seg === '..' || seg.includes(':')) return null;
    cleaned.push(seg);
  }
  return cleaned.length === 0 ? null : cleaned.join('/');
}

export function isToolchainFile(path: string): boolean {
  const name = path.slice(path.lastIndexOf('/') + 1);
  return TOOLCHAIN_FILES.has(name);
}

/** Pick the client-side preview entry point from the file map. */
export function previewEntry(files: Record<string, string>): string | null {
  for (const candidate of ['src/main.tsx', 'src/main.ts', 'src/index.tsx', 'src/index.ts', 'main.tsx']) {
    if (files[candidate] != null) return candidate;
  }
  // Fall back to the first .tsx/.ts under src.
  return Object.keys(files).find((p) => /^src\/.*\.(tsx?|jsx?)$/.test(p)) ?? null;
}
