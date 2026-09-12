import * as esbuild from 'esbuild-wasm';
import wasmURL from 'esbuild-wasm/esbuild.wasm?url';
import { base64ToBytes, bytesToBase64, isBinaryPath, mimeOf } from '../../site-source/binary';

// In-browser bundler for the live preview. It transpiles + bundles the Mode B
// project entirely client-side: local files come from the posted file map;
// bare imports (react, @mui/*, …) are fetched from esm.sh, pinned to the fixed
// palette versions so preview behaviour tracks the offline production build.
// This is single-threaded WASM — no SharedArrayBuffer / COOP-COEP required.

interface BuildRequest {
  id: number;
  files: Record<string, string>;
  entry: string;
  versions: Record<string, string>;
  /** Base URL baked into import.meta.env.VITE_API_BASE_URL so the site's API
   *  client hits the same-origin preview proxy (real, sandboxed tenant data). */
  previewBaseUrl?: string;
}
interface BuildResponse {
  id: number;
  ok: boolean;
  js?: string;
  css?: string;
  /** True when the collected CSS drives Tailwind (compiled in-browser at preview time). */
  usesTailwind?: boolean;
  /** The joined error text, kept as it was for the preview pane's overlay. */
  error?: string;
  /**
   * Every message esbuild reported, with its location intact — errors on a failed build, and
   * warnings on a successful one, which used to be discarded entirely. See `problems.ts`.
   */
  messages?: EsbuildMessage[];
  warnings?: EsbuildMessage[];
}

/** Structurally what esbuild's `Message` is, narrowed to what survives postMessage. */
type EsbuildMessage = import('./problems').EsbuildMessage;

const CDN = 'https://esm.sh';
let ready: Promise<void> | null = null;

// React Router's browser/hash history needs a real document URL. A preview
// iframe is srcdoc (URL "about:srcdoc", history mutation forbidden), so a
// createBrowserRouter app matches no route and 404s. We swap these routers for
// an in-memory one pinned to "/" via a shim module (see the rr-shim onLoad).
const ROUTER_PKGS = new Set(['react-router', 'react-router-dom']);

function init(): Promise<void> {
  ready ??= esbuild.initialize({ wasmURL, worker: false });
  return ready;
}

const LOADER_BY_EXT: Record<string, esbuild.Loader> = {
  ts: 'ts',
  tsx: 'tsx',
  js: 'js',
  jsx: 'jsx',
  mjs: 'js',
  cjs: 'js',
  json: 'json',
  css: 'css',
  svg: 'dataurl', // SVG is stored as text but a JS import of it should be a data URL
};

function loaderFor(path: string): esbuild.Loader {
  // Binary assets (base64 in the map) imported from JS become inline data URLs.
  if (isBinaryPath(path)) return 'dataurl';
  const ext = path.slice(path.lastIndexOf('.') + 1).toLowerCase();
  return LOADER_BY_EXT[ext] ?? 'text';
}

/** Base package name of a bare specifier ("react-dom/client" -> "react-dom"). */
function basePackage(spec: string): string {
  const parts = spec.split('/');
  return spec.startsWith('@') ? parts.slice(0, 2).join('/') : parts[0];
}

/** esm.sh URL for a bare specifier, pinned to the palette version and React-deduped. */
function cdnUrl(spec: string, versions: Record<string, string>): string {
  const base = basePackage(spec);
  const version = versions[base] ? `@${versions[base]}` : '';
  const subpath = spec.slice(base.length); // includes leading slash or ''
  const external =
    base === 'react' ? '' : base === 'react-dom' ? '?external=react' : '?external=react,react-dom';
  return `${CDN}/${base}${version}${subpath}${external}`;
}

function vfsPlugin(
  files: Record<string, string>,
  versions: Record<string, string>,
): esbuild.Plugin {
  return {
    name: 'dcms-vfs',
    setup(build) {
      // CSS imported from JS is a no-op in the JS graph: esbuild-wasm has no
      // Tailwind/PostCSS pipeline, so the project's styles are collected and
      // compiled separately (see collectCss + the preview shell). This also
      // avoids esbuild's "import CSS into JS needs an output path" for the
      // multi-output (js+css) case.
      build.onResolve({ filter: /\.css$/ }, (args) => ({ path: args.path, namespace: 'cssnoop' }));
      build.onLoad({ filter: /.*/, namespace: 'cssnoop' }, () => ({ contents: '', loader: 'js' }));

      // Entry + relative imports from a local file stay in the vfs namespace.
      build.onResolve({ filter: /.*/ }, (args) => {
        if (args.kind === 'entry-point') {
          return { path: args.path.replace(/^\/+/, ''), namespace: 'vfs' };
        }
        // Relative import from a local file.
        if (
          args.namespace === 'vfs' &&
          (args.path.startsWith('./') || args.path.startsWith('../'))
        ) {
          const resolved = resolveRelative(args.importer, args.path, files);
          if (resolved) return { path: resolved, namespace: 'vfs' };
          return { errors: [{ text: `Cannot resolve ${args.path} from ${args.importer}` }] };
        }
        // Relative or absolute-path import from a CDN module. esm.sh emits
        // absolute paths (e.g. "/react@19.2.7/es2022/react.mjs") between its own
        // modules — resolve them against the importer's origin, not as bare specs.
        if (
          args.namespace === 'http' &&
          (args.path.startsWith('./') || args.path.startsWith('../') || args.path.startsWith('/'))
        ) {
          return { path: new URL(args.path, args.importer).href, namespace: 'http' };
        }
        // Absolute CDN URL (esm.sh emits these between packages).
        if (args.path.startsWith('http://') || args.path.startsWith('https://')) {
          return { path: args.path, namespace: 'http' };
        }
        // React Router -> memory-router shim so it resolves inside the iframe.
        if (ROUTER_PKGS.has(args.path)) {
          return { path: args.path, namespace: 'rr-shim' };
        }
        // Bare specifier -> pinned esm.sh URL.
        return { path: cdnUrl(args.path, versions), namespace: 'http' };
      });

      // Re-export the real router, overriding the URL-history factories with the
      // memory equivalent pinned to "/". A local export shadows the same name
      // from `export *`, so everything else passes through unchanged.
      build.onLoad({ filter: /.*/, namespace: 'rr-shim' }, (args) => {
        const real = JSON.stringify(cdnUrl(args.path, versions));
        const contents = `
          export * from ${real};
          import { createMemoryRouter as __mem, MemoryRouter as __MR } from ${real};
          export const createBrowserRouter = (routes, opts = {}) =>
            __mem(routes, { ...opts, initialEntries: opts.initialEntries ?? ['/'] });
          export const createHashRouter = createBrowserRouter;
          export const BrowserRouter = __MR;
          export const HashRouter = __MR;
        `;
        return { contents, loader: 'js', resolveDir: '/' };
      });

      build.onLoad({ filter: /.*/, namespace: 'vfs' }, (args) => {
        const contents = files[args.path];
        if (contents == null) return { errors: [{ text: `File not found: ${args.path}` }] };
        // Binary entries are base64 — hand esbuild the raw bytes so the dataurl
        // loader re-encodes them correctly (a base64 string would double-encode).
        if (isBinaryPath(args.path)) {
          return { contents: base64ToBytes(contents), loader: loaderFor(args.path) };
        }
        return { contents, loader: loaderFor(args.path), resolveDir: '/' };
      });

      build.onLoad({ filter: /.*/, namespace: 'http' }, async (args) => {
        const res = await fetch(args.path);
        if (!res.ok) return { errors: [{ text: `Fetch ${args.path} -> ${res.status}` }] };
        const contents = await res.text();
        const loader: esbuild.Loader = args.path.endsWith('.css') ? 'css' : 'js';
        return { contents, loader };
      });
    },
  };
}

/** Resolve a relative import against the vfs, trying common extensions/index files. */
function resolveRelative(
  importer: string,
  spec: string,
  files: Record<string, string>,
): string | null {
  const dir = importer.includes('/') ? importer.slice(0, importer.lastIndexOf('/')) : '';
  const segments = (dir ? `${dir}/${spec}` : spec).split('/');
  const stack: string[] = [];
  for (const seg of segments) {
    if (seg === '.' || seg === '') continue;
    if (seg === '..') stack.pop();
    else stack.push(seg);
  }
  const guess = stack.join('/');
  const candidates = [
    guess,
    `${guess}.ts`,
    `${guess}.tsx`,
    `${guess}.js`,
    `${guess}.jsx`,
    `${guess}.json`,
    `${guess}.css`,
    `${guess}/index.ts`,
    `${guess}/index.tsx`,
    `${guess}/index.js`,
  ];
  return candidates.find((c) => files[c] != null) ?? null;
}

// Side-effect CSS imports in JS/TS ("import './styles/index.css'").
const CSS_IMPORT_RE = /import\s+["']([^"']+\.css)["'];?/g;
// CSS @import (bare, relative, or url()).
const AT_IMPORT_RE = /@import\s+(?:url\()?["']([^"']+)["']\)?[^;]*;/g;
// Tailwind v4 @source globs reference the filesystem; the browser engine scans
// the live DOM instead, so they are dropped from the preview stylesheet.
const AT_SOURCE_RE = /@source\b[^;]*;/g;
// url(...) references in CSS — rewritten to data URIs so relative assets
// (fonts, background images) resolve inside the srcdoc iframe.
const CSS_URL_RE = /url\(\s*(['"]?)([^'")]+)\1\s*\)/g;

/** Inline relative url() assets from a stylesheet as data URIs (fonts, images). */
function rewriteCssUrls(css: string, cssPath: string, files: Record<string, string>): string {
  return css.replace(CSS_URL_RE, (full, _quote: string, spec: string) => {
    if (!spec.startsWith('./') && !spec.startsWith('../')) return full; // leave remote/data/absolute
    const clean = spec.split(/[?#]/)[0]; // drop cache-busting query/fragment
    const resolved = resolveRelative(cssPath, clean, files);
    if (!resolved || files[resolved] == null) return full;
    const base64 = isBinaryPath(resolved)
      ? files[resolved]
      : bytesToBase64(new TextEncoder().encode(files[resolved]));
    return `url("data:${mimeOf(resolved)};base64,${base64}")`;
  });
}

/** Order JS files so the entry (main.*) is scanned first, then shallowest paths. */
function cssScanRank(path: string): number {
  if (/(^|\/)main\.[jt]sx?$/.test(path)) return 0;
  return 1 + path.split('/').length;
}

/**
 * Gather the project's stylesheet for the preview. esbuild does not touch CSS
 * (see cssnoop), so here we walk every side-effect CSS import from the JS graph,
 * inline the relative `@import` chain from the vfs, keep a bare
 * `@import 'tailwindcss'` for the in-browser Tailwind engine (dropping its
 * `source(none)` and `@source` directives so it auto-scans the DOM), and report
 * whether Tailwind is in play.
 */
function collectCss(files: Record<string, string>): { css: string; usesTailwind: boolean } {
  const jsRe = /\.(tsx?|jsx?|mjs|cjs)$/;
  const roots: string[] = [];
  const jsFiles = Object.keys(files)
    .filter((p) => jsRe.test(p))
    .sort((a, b) => cssScanRank(a) - cssScanRank(b));
  for (const js of jsFiles) {
    CSS_IMPORT_RE.lastIndex = 0;
    let m: RegExpExecArray | null;
    while ((m = CSS_IMPORT_RE.exec(files[js]))) {
      const resolved = resolveRelative(js, m[1], files);
      if (resolved && !roots.includes(resolved)) roots.push(resolved);
    }
  }

  let usesTailwind = false;
  const seen = new Set<string>();
  const inline = (path: string): string => {
    if (seen.has(path)) return '';
    seen.add(path);
    const raw = files[path];
    if (raw == null) return '';
    let css = raw.replace(AT_IMPORT_RE, (full, spec: string) => {
      if (/^tailwindcss(\/|$)/.test(spec)) {
        usesTailwind = true;
        return `@import "${spec}";`; // strip source(none)/media so the engine scans the DOM
      }
      if (spec.startsWith('./') || spec.startsWith('../')) {
        const resolved = resolveRelative(path, spec, files);
        return resolved ? inline(resolved) : '';
      }
      return full; // leave remote/url() imports untouched
    });
    css = css.replace(AT_SOURCE_RE, '');
    css = rewriteCssUrls(css, path, files);
    if (/@tailwind\b/.test(css)) usesTailwind = true;
    return `${css}\n`;
  };

  let out = '';
  for (const root of roots) out += inline(root);
  return { css: out.trim(), usesTailwind };
}

self.onmessage = async (e: MessageEvent<BuildRequest>) => {
  const { id, files, entry, versions, previewBaseUrl } = e.data;
  const respond = (r: BuildResponse) => (self as unknown as Worker).postMessage(r);
  const importMetaEnv = JSON.stringify({
    MODE: 'development',
    DEV: true,
    PROD: false,
    SSR: false,
    BASE_URL: '/',
    VITE_API_BASE_URL: previewBaseUrl ?? '',
  });
  try {
    await init();
    const result = await esbuild.build({
      entryPoints: [entry],
      bundle: true,
      write: false,
      format: 'esm',
      jsx: 'automatic',
      jsxImportSource: 'react',
      target: 'es2020',
      sourcemap: false,
      plugins: [vfsPlugin(files, versions)],
      define: {
        'process.env.NODE_ENV': '"development"',
        // Vite normally substitutes import.meta.env at build time; esbuild does
        // not, so a site reading import.meta.env.VITE_* would throw at runtime.
        // Provide the base shape + the preview API base; unknown VITE_* keys read
        // as undefined, which site code guards with `?? default`.
        'import.meta.env': importMetaEnv,
      },
    });
    let js = '';
    for (const out of result.outputFiles ?? []) {
      if (!out.path.endsWith('.css')) js += out.text;
    }
    const { css, usesTailwind } = collectCss(files);
    // A build can succeed and still have something to say. These were thrown away before, so
    // an unused import or a suspicious `==` was reported to nobody.
    respond({ id, ok: true, js, css, usesTailwind, warnings: asMessages(result.warnings) });
  } catch (err) {
    const errors =
      err && typeof err === 'object' && 'errors' in err
        ? asMessages((err as { errors: unknown[] }).errors)
        : [{ text: String((err as Error)?.message ?? err) }];

    respond({
      id,
      ok: false,
      // Kept exactly as it was: the preview pane's overlay reads this, and a build failure
      // must keep saying what went wrong even where nothing consumes the structured form.
      error: errors.map((e) => e.text).join('\n'),
      messages: errors,
      warnings: [],
    });
  }
};

/**
 * esbuild's messages, stripped to what can cross a worker boundary.
 *
 * <p>`postMessage` uses structured clone, which throws on anything holding a function — and
 * esbuild's `Message` carries `notes` with nested objects that have varied between versions.
 * Copying the four fields that are read is both smaller and immune to that.</p>
 */
function asMessages(raw: readonly unknown[] | undefined): EsbuildMessage[] {
  return (raw ?? []).map((entry) => {
    const m = entry as { text?: unknown; location?: Record<string, unknown> | null };
    const location = m.location;
    return {
      text: typeof m.text === 'string' ? m.text : String(m.text ?? 'Build failed'),
      location: location
        ? {
            file: typeof location.file === 'string' ? location.file : undefined,
            line: typeof location.line === 'number' ? location.line : undefined,
            column: typeof location.column === 'number' ? location.column : undefined,
            lineText: typeof location.lineText === 'string' ? location.lineText : undefined,
          }
        : null,
    };
  });
}

export type { BuildRequest, BuildResponse };
