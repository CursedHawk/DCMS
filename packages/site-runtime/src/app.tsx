import { useEffect, useMemo } from 'react';
import { Outlet, RouterProvider, createBrowserRouter, useLocation, useParams, type RouteObject } from 'react-router';
import { guardedRoutes } from './access';
import { codeDefinitions, readCodeContracts, type CodeContract, type CodeModules } from './code';
import { BUILTIN_COMPONENTS } from './components';
import { appSchema, pageSchema, type App, type Page } from './document';
import { DataClientContext, ItemContext, itemParser, itemPath, parseItem, useData, type DataClient } from './data';
import { applyHead } from './head';
import { APP_JSON, THEME_JSON, pageIdFromPath } from './paths';
import type { Registry } from './registry';
import { RenderNode } from './render';
import { SiteContext } from './site';
import { PageStateContext } from './state';
import { RUNTIME_CSS } from './styles';
import { readComponentDocs, siteRegistry, type TenantComponentDoc } from './tenant';
import { emptyTheme, themeRootCss, themeTokensSchema, type ThemeTokens } from './theme';

/**
 * A Mode D site as a running React application.
 *
 * The documents are read at build time (`import.meta.glob` in the generated `src/main.tsx`) and
 * validated here, once, with the same schemas the builder uses. A document that fails is
 * reported and left out rather than taking the site down: the builder and the pre-publish check
 * are where an author hears about it; a visitor should still get every page that is fine.
 */

export interface SiteDocuments {
  app: App;
  pages: Readonly<Record<string, Page>>;
  theme: ThemeTokens;
  /** The site's own components (dcms/components/<name>/v<N>.json), every version. */
  components: readonly TenantComponentDoc[];
  /** Developer components' contracts (dcms/code/<name>.json). */
  code: readonly CodeContract[];
  /** Their implementations, where code may run (the site, the sandboxed preview). */
  codeModules?: CodeModules;
  /** What could not be read, for the console. */
  problems: readonly string[];
}

/** Repo path → parsed JSON, keyed however the bundler names them (`../dcms/app.json`, `/dcms/...`). */
export function loadDocuments(modules: Readonly<Record<string, unknown>>, codeModules?: CodeModules): SiteDocuments {
  const problems: string[] = [];
  const byPath = new Map<string, unknown>();
  for (const [key, value] of Object.entries(modules)) {
    const at = key.lastIndexOf('dcms/');
    if (at >= 0) byPath.set(key.slice(at), value);
  }

  const parsedApp = appSchema.safeParse(byPath.get(APP_JSON));
  if (!parsedApp.success) problems.push(`${APP_JSON}: ${parsedApp.error.issues[0]?.message ?? 'missing'}`);
  const app: App = parsedApp.success ? parsedApp.data : { schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] };

  const parsedTheme = themeTokensSchema.safeParse(byPath.get(THEME_JSON) ?? {});
  const theme = parsedTheme.success ? parsedTheme.data : emptyTheme();
  if (!parsedTheme.success) problems.push(`${THEME_JSON}: ${parsedTheme.error.issues[0]?.message}`);

  const pages: Record<string, Page> = {};
  for (const [path, json] of byPath) {
    const id = pageIdFromPath(path);
    if (!id) continue;
    const page = pageSchema.safeParse(json);
    if (page.success) pages[id] = page.data;
    else problems.push(`${path}: ${page.error.issues[0]?.message}`);
  }
  const components = readComponentDocs(byPath);
  for (const p of components.problems) problems.push(`${p.path}: ${p.message}`);
  const code = readCodeContracts(byPath);
  for (const p of code.problems) problems.push(`${p.path}: ${p.message}`);
  return { app, pages, theme, components: components.docs, code: code.contracts, codeModules, problems };
}

/** The registry a site renders with: the built-ins and the site's own components. */
/** Each route's page title by path, for breadcrumbs. */
export function routeTitles(documents: SiteDocuments): Record<string, string> {
  return Object.fromEntries(documents.app.routes.flatMap((r) => (documents.pages[r.page] ? [[r.path, documents.pages[r.page]!.title]] : [])));
}

export function registryFor(documents: SiteDocuments): Registry {
  return siteRegistry([...BUILTIN_COMPONENTS, ...codeDefinitions(documents.code, documents.codeModules)], documents.components).registry;
}

/** The routes of a site, for a browser router on the site and a memory router in the builder's preview. */
export function siteRoutes(documents: SiteDocuments, registry: Registry = registryFor(documents)): RouteObject[] {
  const children: RouteObject[] = documents.app.routes.map((route) => ({
    path: route.path,
    element: <RoutePage page={documents.pages[route.page]} app={documents.app} registry={registry} />,
  }));
  children.push({ path: '*', element: <NotFound /> });
  return [{ element: <Shell app={documents.app} registry={registry} />, children }];
}

/** The site's stylesheet and theme variables, as elements so they travel with the tree. */
export function SiteStyles({ theme }: { theme: ThemeTokens }) {
  return (
    <>
      <style>{RUNTIME_CSS}</style>
      <style>{themeRootCss(theme)}</style>
    </>
  );
}

export function DcmsApp({ documents, registry, dataClient }: { documents: SiteDocuments; registry?: Registry; dataClient?: DataClient }) {
  const router = useMemo(() => {
    // Guarded by the site's access rules: the app's own navigation never reaches the edge.
    const { routes, hydrationData } = guardedRoutes(siteRoutes(documents, registry ?? registryFor(documents)));
    return createBrowserRouter(routes, { hydrationData });
  }, [documents, registry]);
  useEffect(() => {
    for (const problem of documents.problems) console.error(`dcms: ${problem}`);
  }, [documents]);
  const site = (
    <SiteContext.Provider value={{ app: documents.app, titles: routeTitles(documents) }}>
      <SiteStyles theme={documents.theme} />
      <RouterProvider router={router} />
    </SiteContext.Provider>
  );
  return dataClient ? <DataClientContext.Provider value={dataClient}>{site}</DataClientContext.Provider> : site;
}

/** The shell around every route: the app's own `shell` tree, whose outlet the page renders into. */
function Shell({ app, registry }: { app: App; registry: Registry }) {
  return app.shell ? <RenderNode node={app.shell} registry={registry} /> : <Outlet />;
}

function RoutePage({ page, app, registry }: { page: Page | undefined; app: App; registry: Registry }) {
  const params = useParams();
  const slug = page?.data ? params[page.data.param] : undefined;
  const item = useData(page?.data && slug ? itemPath(page.data.source, slug) : null, page?.data && slug ? itemParser(page.data.source, slug) : parseItem);
  const scope = useMemo(
    () => (page?.data && item.state === 'ready' ? { item: item.value, index: 0, count: 1 } : null),
    [page, item],
  );

  useEffect(() => {
    if (page && (!page.data || scope)) applyHead(document, app, page, scope);
  }, [app, page, params, scope]);

  // A link to /#pricing lands on the section with that anchor, once the page has drawn it.
  const { hash } = useLocation();
  useEffect(() => {
    if (hash.length > 1) document.getElementById(decodeURIComponent(hash.slice(1)))?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, [hash, scope]);

  if (!page) return <NotFound />;
  const stateScope = { pageId: page.id, defaults: page.state ?? {} };
  if (page.data) {
    // A detail page with no such item is a 404, not an empty page.
    if (!slug || item.state === 'error') return <NotFound />;
    if (item.state === 'loading') return <main className="dcms-page" aria-busy="true" />;
  }
  return (
    <PageStateContext.Provider value={stateScope}>
      <ItemContext.Provider value={scope}>
        <RenderNode node={page.root} registry={registry} />
      </ItemContext.Provider>
    </PageStateContext.Provider>
  );
}

function NotFound() {
  return (
    <main className="dcms-page dcms-not-found">
      <h1 className="dcms-heading dcms-heading-2">Page not found</h1>
    </main>
  );
}
