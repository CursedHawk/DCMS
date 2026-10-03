/**
 * `import.meta.glob` for the in-browser preview bundler. Vite expands it at build time; esbuild
 * has no equivalent, and a Mode D site's entry reads its pages and developer components with it.
 *
 * Supports what DCMS templates use: a string-literal relative pattern with `*` and `**`, and
 * `{ eager: true }` with an optional `import: 'default'`. A lazy glob becomes functions returning
 * the already-bundled module, which behaves the same for a preview. Keys are the relative paths
 * as written, exactly as Vite produces them.
 */
const GLOB_CALL = /import\.meta\.glob\(\s*(['"])([^'"]+)\1\s*(?:,\s*(\{[^}]*\}))?\s*\)/g;

export function expandImportGlobs(code: string, importer: string, paths: readonly string[]): string {
  if (!code.includes('import.meta.glob')) return code;
  const imports: string[] = [];
  let n = 0;
  const out = code.replace(GLOB_CALL, (_all, _q: string, pattern: string, options: string | undefined) => {
    const eager = /eager\s*:\s*true/.test(options ?? '');
    const pick = /import\s*:\s*(['"])default\1/.test(options ?? '') ? '.default' : '';
    const entries = matchGlob(pattern, importer, paths).map(({ key }) => {
      const id = `__dcms_glob_${n++}`;
      imports.push(`import * as ${id} from ${JSON.stringify(key)};`);
      const value = `${id}${pick}`;
      return `${JSON.stringify(key)}: ${eager ? value : `() => Promise.resolve(${value})`}`;
    });
    return `({ ${entries.join(', ')} })`;
  });
  return `${imports.join('\n')}\n${out}`;
}

/** The files a relative glob matches, with the key Vite would give each. */
export function matchGlob(pattern: string, importer: string, paths: readonly string[]): { key: string; path: string }[] {
  if (!pattern.startsWith('./') && !pattern.startsWith('../')) return [];
  const segments = pattern.split('/');
  const firstGlob = segments.findIndex((s) => s.includes('*'));
  const staticPart = firstGlob < 0 ? pattern : `${segments.slice(0, firstGlob).join('/')}/`;
  const base = resolve(importer, staticPart);
  if (base === null) return [];
  const rest = firstGlob < 0 ? '' : segments.slice(firstGlob).join('/');
  const regex = new RegExp(`^${escape(base)}${toRegex(rest)}$`);
  return paths
    .filter((p) => regex.test(p))
    .sort()
    .map((path) => ({ key: staticPart + path.slice(base.length), path }));
}

/** `importer`'s directory joined with a relative spec, or null if it climbs out of the project. */
function resolve(importer: string, spec: string): string | null {
  const stack = importer.split('/').slice(0, -1);
  for (const seg of spec.split('/')) {
    if (seg === '' || seg === '.') continue;
    if (seg === '..') {
      if (!stack.length) return null;
      stack.pop();
    } else stack.push(seg);
  }
  const joined = stack.join('/');
  return spec.endsWith('/') && joined ? `${joined}/` : joined;
}

function toRegex(glob: string): string {
  return glob
    .split('/')
    .map((seg) => (seg === '**' ? '(?:[^/]+/)*' : `${seg.split('*').map(escape).join('[^/]*')}/`))
    .join('')
    .replace(/\/$/, '');
}

function escape(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
