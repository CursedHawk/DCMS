// The pages of a Mode B site, read from its own React Router routes (#343).
//
// site-builder writes this file into a Mode B build's work dir as .dcms-sitemap.mjs and runs it
// with node after `vite build`, inside the same sandbox (no network). It loads src/routes.tsx with
// the site's own Vite, catches the route array handed to createBrowserRouter, and writes the pages
// to .dcms-sitemap.json for the builder to check and store. It never fails the build: anything it
// cannot read leaves the sitemap at "/".
//
// A route declares itself with React Router's `handle`:
//   handle: { sitemap: false }                              this page and those under it stay out
//   handle: { sitemap: { instance, contentType } }          a detail route: one URL per published item
//   handle: { sitemap: [{ path, instance?, contentType? }] } these entries instead of the route's own
// and the starter template's `:instance/:contentType(/:slug)` routes list every collection.

import { existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const ENTRIES = ['src/routes.tsx', 'src/routes.ts', 'src/routes.jsx', 'src/routes.js'];
const OUTPUT = '.dcms-sitemap.json';
const VIRTUAL = '\0dcms-sitemap-router:';

/** `/a` + `b` → `/a/b`; an absolute child path stands alone. */
function joinPath(base, path) {
  const joined = path.startsWith('/') ? path : `${base.replace(/\/+$/, '')}/${path}`;
  const clean = `/${joined.split('/').filter(Boolean).join('/')}`;
  return clean;
}

function isBinding(value) {
  return value && typeof value === 'object' && typeof value.instance === 'string' && typeof value.contentType === 'string';
}

/**
 * Every page the routes declare. `collections` is the generated `src/api` list
 * (`{ instance, contentType }[]`) the starter template's generic routes expand over.
 */
export function pagesFromRoutes(routes, collections = []) {
  const pages = new Map();
  const add = (page) => {
    if (!pages.has(page.path)) pages.set(page.path, page);
  };

  const own = (path, sitemap) => {
    const segments = path.split('/').filter(Boolean);
    if (segments.some((s) => s.includes('*') || s.endsWith('?'))) return;
    const params = segments.filter((s) => s.startsWith(':'));
    if (params.length === 0) {
      add({ path });
      return;
    }
    if (isBinding(sitemap)) {
      if (params.length === 1 && segments.at(-1).startsWith(':')) {
        add({ path, instance: sitemap.instance, contentType: sitemap.contentType });
      }
      return;
    }
    // The starter template's convention: a collection's listing and its items, for every collection.
    const n = segments.length;
    const tail = (k) => `/${segments.slice(0, n - k).join('/')}`.replace(/\/$/, '');
    if (params.length === 2 && segments[n - 2] === ':instance' && segments[n - 1] === ':contentType') {
      for (const c of collections) if (isBinding(c)) add({ path: `${tail(2)}/${c.instance}/${c.contentType}` });
    } else if (params.length === 3 && segments[n - 3] === ':instance' && segments[n - 2] === ':contentType' && segments[n - 1].startsWith(':')) {
      for (const c of collections) {
        if (isBinding(c)) add({ path: `${tail(3)}/${c.instance}/${c.contentType}/:slug`, instance: c.instance, contentType: c.contentType });
      }
    }
  };

  const walk = (list, base) => {
    if (!Array.isArray(list)) return;
    for (const route of list) {
      if (!route || typeof route !== 'object') continue;
      const sitemap = route.handle?.sitemap;
      if (sitemap === false) continue;
      const path = route.index || typeof route.path !== 'string' ? base : joinPath(base, route.path);
      if (Array.isArray(sitemap)) {
        for (const entry of sitemap) {
          if (entry && typeof entry.path === 'string') {
            add(isBinding(entry)
              ? { path: joinPath('/', entry.path), instance: entry.instance, contentType: entry.contentType }
              : { path: joinPath('/', entry.path) });
          }
        }
      } else if (route.index || typeof route.path === 'string') {
        own(path, sitemap);
      }
      walk(route.children, path);
    }
  };

  walk(routes, '/');
  return [...pages.values()];
}

/**
 * `react-router` with createBrowserRouter/createHashRouter swapped for a catch of their routes:
 * the real ones need a browser, and the routes are all that is wanted here. The IDE preview swaps
 * routers the same way.
 */
function captureRouter() {
  return {
    name: 'dcms-sitemap-capture',
    enforce: 'pre',
    resolveId(id, importer) {
      if ((id === 'react-router' || id === 'react-router-dom') && !importer?.startsWith(VIRTUAL)) return VIRTUAL + id;
      return undefined;
    },
    load(id) {
      if (!id.startsWith(VIRTUAL)) return undefined;
      const real = JSON.stringify(id.slice(VIRTUAL.length));
      return `export * from ${real};
const capture = (routes) => { globalThis.__dcmsSitemapRoutes = routes; return { routes }; };
export { capture as createBrowserRouter, capture as createHashRouter };`;
    },
  };
}

/** The pages of the project at `root`, and a line saying where they came from. */
export async function listPages(root) {
  const entry = ENTRIES.find((file) => existsSync(join(root, file)));
  if (!entry) {
    return { pages: [{ path: '/' }], note: 'no src/routes.tsx, so only / is listed. Declare pages there with createBrowserRouter.' };
  }
  const { createServer } = await import('vite');
  const server = await createServer({
    root,
    appType: 'custom',
    logLevel: 'error',
    clearScreen: false,
    server: { middlewareMode: true, hmr: false, watch: null },
    optimizeDeps: { noDiscovery: true, include: [] },
    // A bare import from node_modules is loaded by Node straight away in SSR, past every plugin;
    // through the pipeline instead, so captureRouter can swap the router.
    ssr: { noExternal: ['react-router', 'react-router-dom'] },
    plugins: [captureRouter()],
  });
  try {
    globalThis.__dcmsSitemapRoutes = undefined;
    await server.ssrLoadModule(`/${entry}`);
    const routes = globalThis.__dcmsSitemapRoutes;
    if (!Array.isArray(routes)) {
      return { pages: [{ path: '/' }], note: `${entry} makes no router with createBrowserRouter, so only / is listed.` };
    }
    let collections = [];
    if (existsSync(join(root, 'src/api/index.ts'))) {
      try {
        const api = await server.ssrLoadModule('/src/api/index.ts');
        if (Array.isArray(api.collections)) collections = api.collections;
      } catch {
        // No collections to expand over; the rest of the routes still count.
      }
    }
    const pages = pagesFromRoutes(routes, collections);
    return { pages, note: `${pages.length} page(s) from ${entry}.` };
  } finally {
    await server.close();
  }
}

async function main() {
  const root = process.cwd();
  try {
    const { pages, note } = await listPages(root);
    writeFileSync(join(root, OUTPUT), JSON.stringify(pages));
    console.log(`Sitemap: ${note}`);
  } catch (error) {
    console.log(`Sitemap: the routes could not be read (${error?.message ?? error}), so only / is listed.`);
  }
  // A module the app loaded may hold a timer or socket open; the work is done either way.
  process.exit(0);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await main();
}
