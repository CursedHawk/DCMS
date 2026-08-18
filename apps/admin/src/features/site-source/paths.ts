// Small helpers shared by every DCMS source editor: path normalization (mirroring
// the backend's StaticSiteFiles.NormalizeEntryPath so a save is never rejected),
// language-id mapping, and the file:/// URI scheme Monaco models live under.

// A Mode B site is a complete, self-contained project and owns its own package.json
// + lockfile, so nothing is locked as "platform toolchain" anymore. Kept as an empty
// set (rather than deleting the concept) so call sites stay simple.
export const TOOLCHAIN_FILES = new Set<string>([]);

/**
 * Paths the owning surface generates and would overwrite on the next save, so the
 * editor shows them read-only. Registered by the surface (the Mode A builder
 * generates styles/theme.css from site.json); empty for the Mode B IDE.
 */
let generatedPaths: (path: string) => boolean = () => false;

export function setGeneratedPathPredicate(predicate: (path: string) => boolean): void {
  generatedPaths = predicate;
}

export function isGeneratedFile(path: string): boolean {
  return generatedPaths(path);
}

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
