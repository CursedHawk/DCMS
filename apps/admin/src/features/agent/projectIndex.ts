import { isBinaryPath } from '../site-source/binary';

/**
 * A structural map of the site, so the agent can answer "where is X" without reading files.
 *
 * <p>This is the cheapest token saving available. Without it, finding the component behind a
 * request means listing every path and opening the plausible ones; with it, the answer is a
 * lookup and the model opens exactly one file. The index costs nothing the model pays for — it
 * is built in the browser from a file map the browser already holds.</p>
 *
 * <h3>It is a scanner, not a parser</h3>
 * <p>Regular expressions over source, deliberately. A real parser would be more accurate and
 * would cost a dependency, a worker, and an async boundary in the middle of a synchronous store;
 * for "which file defines `Hero`" the scanner is right nearly always and wrong cheaply — it can
 * miss a definition, which costs one extra search, and it can report one inside a comment or a
 * string, which costs one wasted read. Neither is a correctness risk, because nothing here
 * decides what to edit. Everything the agent actually changes goes through a hash-guarded patch
 * against real file content.</p>
 *
 * <p><b>No worker.</b> The plan called for one by analogy with the esbuild bundler, but that
 * runs in a worker because WASM compilation genuinely blocks; a regex sweep of a few hundred
 * files is single-digit milliseconds, and incremental updates re-scan exactly one file. A worker
 * would add a message boundary and an await to save nothing measurable.</p>
 */
export interface ProjectIndex {
  /** Per-file facts, keyed by path. */
  files: Record<string, FileFacts>;
  /** Symbol name → everywhere it appears to be defined. */
  definitions: Map<string, SymbolDef[]>;
  /** Module specifier → the files importing it. Bare specifiers and relative paths alike. */
  importers: Map<string, string[]>;
  /** From package.json, when the site has one. */
  dependencies: Record<string, string>;
  /** The app entry, if one of the conventional paths exists. */
  entry: string | null;
  /** Route paths found in router configuration, deduplicated and sorted. */
  routes: string[];
}

export interface FileFacts {
  imports: string[];
  definitions: SymbolDef[];
  /** Definitions that look like React components: capitalised, and the file contains JSX. */
  components: string[];
  routes: string[];
  cssClasses: string[];
  cssVars: string[];
}

export interface SymbolDef {
  name: string;
  path: string;
  /** 1-based. */
  line: number;
  kind: 'function' | 'const' | 'class' | 'type' | 'component';
  exported: boolean;
}

const CODE = /\.(tsx?|jsx?|mjs|cjs)$/;
const STYLE = /\.(css|scss|less)$/;

/** Conventional entry points, most specific first. */
const ENTRIES = ['src/main.tsx', 'src/main.ts', 'src/index.tsx', 'src/index.ts', 'src/App.tsx'];

export function buildIndex(files: Readonly<Record<string, string>>): ProjectIndex {
  const index: ProjectIndex = {
    files: {},
    definitions: new Map(),
    importers: new Map(),
    dependencies: readDependencies(files['package.json']),
    entry: ENTRIES.find((p) => files[p] !== undefined) ?? null,
    routes: [],
  };
  for (const path of Object.keys(files)) {
    index.files[path] = scanFile(path, files[path]);
  }
  rederive(index);
  return index;
}

/**
 * Re-scan one file in place.
 *
 * <p>Pass `null` for a deletion. This is what keeps the index honest during a run: the agent
 * patches a file and the next symbol lookup reflects it, without a full sweep.</p>
 */
export function updateIndex(
  index: ProjectIndex,
  path: string,
  content: string | null,
): ProjectIndex {
  if (content === null) delete index.files[path];
  else index.files[path] = scanFile(path, content);

  if (path === 'package.json') {
    index.dependencies = readDependencies(content ?? undefined);
  }
  // The entry can appear or vanish with the file that defines it.
  index.entry = ENTRIES.find((p) => index.files[p] !== undefined) ?? null;
  rederive(index);
  return index;
}

/** Rebuild the cross-file maps from the per-file facts. Cheap: it is a walk over small arrays. */
function rederive(index: ProjectIndex): void {
  index.definitions = new Map();
  index.importers = new Map();
  const routes = new Set<string>();

  for (const [path, facts] of Object.entries(index.files)) {
    for (const def of facts.definitions) {
      const list = index.definitions.get(def.name);
      if (list) list.push(def);
      else index.definitions.set(def.name, [def]);
    }
    for (const spec of facts.imports) {
      const list = index.importers.get(spec);
      if (list) {
        if (!list.includes(path)) list.push(path);
      } else {
        index.importers.set(spec, [path]);
      }
    }
    for (const route of facts.routes) routes.add(route);
  }

  index.routes = [...routes].sort();
}

const EMPTY: FileFacts = {
  imports: [],
  definitions: [],
  components: [],
  routes: [],
  cssClasses: [],
  cssVars: [],
};

function scanFile(path: string, content: string): FileFacts {
  if (isBinaryPath(path)) return EMPTY;
  if (STYLE.test(path)) return scanStyles(content);
  if (!CODE.test(path)) return EMPTY;
  return scanCode(path, content);
}

// `import x from 'y'`, `import 'y'`, `export … from 'y'`, `require('y')`, `import('y')`.
const IMPORT_RE =
  /(?:^\s*(?:import|export)\b[\s\S]*?from\s*['"]([^'"]+)['"]|^\s*import\s*['"]([^'"]+)['"]|\brequire\(\s*['"]([^'"]+)['"]\s*\)|\bimport\(\s*['"]([^'"]+)['"]\s*\))/gm;

const DEF_RES: { re: RegExp; kind: SymbolDef['kind'] }[] = [
  { re: /^\s*(export\s+)?(?:async\s+)?function\s+\*?([A-Za-z_$][\w$]*)/, kind: 'function' },
  { re: /^\s*(export\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*[=:]/, kind: 'const' },
  { re: /^\s*(export\s+)?class\s+([A-Za-z_$][\w$]*)/, kind: 'class' },
  { re: /^\s*(export\s+)?(?:type|interface|enum)\s+([A-Za-z_$][\w$]*)/, kind: 'type' },
  { re: /^\s*(export\s+)?default\s+(?:async\s+)?function\s+([A-Za-z_$][\w$]*)/, kind: 'function' },
];

// `path="/about"` and `path: '/about'`, which covers both JSX <Route> and object route configs.
const ROUTE_RE = /\bpath\s*[=:]\s*['"{]?['"]([^'"]*)['"]/g;

/** JSX anywhere in the file, used only to decide whether a capitalised symbol is a component. */
const JSX_RE = /<[A-Za-z][\w.]*[\s/>]/;

function scanCode(path: string, content: string): FileFacts {
  const facts: FileFacts = {
    imports: [],
    definitions: [],
    components: [],
    routes: [],
    cssClasses: [],
    cssVars: [],
  };

  for (const m of content.matchAll(IMPORT_RE)) {
    const spec = m[1] ?? m[2] ?? m[3] ?? m[4];
    if (spec && !facts.imports.includes(spec)) facts.imports.push(spec);
  }

  for (const m of content.matchAll(ROUTE_RE)) {
    // An empty path is a legitimate index route; a `path` prop on something that is not a route
    // (an <svg path>, say) is the false positive this accepts in exchange for not parsing.
    if (m[1] !== undefined && !facts.routes.includes(m[1])) facts.routes.push(m[1]);
  }

  const hasJsx = JSX_RE.test(content);
  const lines = content.split('\n');
  const seen = new Set<string>();

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    // Cheap pre-filter: the overwhelming majority of lines declare nothing.
    if (!/\b(function|const|let|var|class|type|interface|enum|default)\b/.test(line)) continue;

    for (const { re, kind } of DEF_RES) {
      const m = re.exec(line);
      if (!m) continue;
      const name = m[2];
      // One definition per name per file: a `const x` reassigned in a block is not a second
      // place to look, and duplicates make the symbol index lie about how many there are.
      if (seen.has(name)) break;
      seen.add(name);

      const isComponent =
        hasJsx && /^[A-Z]/.test(name) && (kind === 'function' || kind === 'const');
      facts.definitions.push({
        name,
        path,
        line: i + 1,
        kind: isComponent ? 'component' : kind,
        exported: Boolean(m[1]) || /^\s*export\s+default\b/.test(line),
      });
      if (isComponent) facts.components.push(name);
      break;
    }
  }

  return facts;
}

const CLASS_RE = /\.(-?[A-Za-z_][\w-]*)/g;
const VAR_RE = /(--[A-Za-z_][\w-]*)\s*:/g;

function scanStyles(content: string): FileFacts {
  const cssClasses = new Set<string>();
  const cssVars = new Set<string>();
  for (const m of content.matchAll(CLASS_RE)) cssClasses.add(m[1]);
  for (const m of content.matchAll(VAR_RE)) cssVars.add(m[1]);
  return {
    imports: [],
    definitions: [],
    components: [],
    routes: [],
    cssClasses: [...cssClasses].sort(),
    cssVars: [...cssVars].sort(),
  };
}

function readDependencies(packageJson: string | undefined): Record<string, string> {
  if (!packageJson) return {};
  try {
    const parsed = JSON.parse(packageJson) as {
      dependencies?: Record<string, string>;
      devDependencies?: Record<string, string>;
    };
    return { ...parsed.dependencies, ...parsed.devDependencies };
  } catch {
    // A package.json the agent is midway through editing is routinely unparseable. Reporting no
    // dependencies beats throwing inside an index rebuild triggered by a keystroke.
    return {};
  }
}

// ---------------------------------------------------------------------------
// Queries
// ---------------------------------------------------------------------------

/** Where a symbol is defined. Empty when the scanner has never seen the name. */
export function findDefinitions(index: ProjectIndex, name: string): SymbolDef[] {
  return index.definitions.get(name) ?? [];
}

/**
 * Files that import a module.
 *
 * <p>Resolves a bare name (`react`) directly, and a local symbol by finding the file that defines
 * it and matching the relative specifiers that land on it. Extension-less and `/index` forms both
 * count, because that is how they are written.</p>
 */
export function findImporters(index: ProjectIndex, target: string): string[] {
  const direct = index.importers.get(target);
  if (direct) return [...direct].sort();

  const out = new Set<string>();
  const stem = target.replace(/\.(tsx?|jsx?)$/, '').replace(/\/index$/, '');
  for (const [spec, paths] of index.importers) {
    if (!spec.startsWith('.')) continue;
    const specStem = spec.replace(/\.(tsx?|jsx?)$/, '').replace(/\/index$/, '');
    // A relative specifier is resolved against its importer, so compare by trailing segment
    // rather than trying to re-implement module resolution here.
    if (
      specStem.endsWith(`/${stem.split('/').pop()}`) ||
      specStem === `./${stem.split('/').pop()}`
    ) {
      for (const p of paths) out.add(p);
    }
  }
  return [...out].sort();
}

/**
 * The project summary the model is given once, instead of a file list.
 *
 * <p>Everything a coding agent normally asks three tool calls to establish — what framework,
 * where the entry is, what routes exist, what it may import — in a few dozen tokens.</p>
 */
export function summarize(index: ProjectIndex): string {
  const parts: string[] = [];
  const fileCount = Object.keys(index.files).length;
  parts.push(`${fileCount} files${index.entry ? `, entry ${index.entry}` : ''}`);

  const deps = Object.keys(index.dependencies).sort();
  if (deps.length) parts.push(`dependencies: ${deps.join(', ')}`);

  if (index.routes.length) parts.push(`routes: ${index.routes.join(' ')}`);

  const components = Object.values(index.files)
    .flatMap((f) => f.components)
    .sort();
  if (components.length) {
    // Capped: a large site's component list is itself a budget item, and the model can search
    // for the rest. Twenty is enough to recognise the shape of the app.
    const shown = components.slice(0, 20);
    parts.push(
      `components: ${shown.join(', ')}${components.length > shown.length ? `, +${components.length - shown.length} more` : ''}`,
    );
  }

  return parts.join('\n');
}
