import { useEffect, useMemo } from 'react';
import { Outlet, RouterProvider, createBrowserRouter, useParams, type RouteObject } from 'react-router';
import { BUILTIN_COMPONENTS } from './components';
import { appSchema, pageSchema, type App, type Page } from './document';
import { applyHead } from './head';
import { APP_JSON, THEME_JSON, pageIdFromPath } from './paths';
import type { Registry } from './registry';
import { RenderNode } from './render';
import { SiteContext } from './site';
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
  /** What could not be read, for the console. */
  problems: readonly string[];
}

/** Repo path → parsed JSON, keyed however the bundler names them (`../dcms/app.json`, `/dcms/...`). */
export function loadDocuments(modules: Readonly<Record<string, unknown>>): SiteDocuments {
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
  return { app, pages, theme, components: components.docs, problems };
}

/** The registry a site renders with: the built-ins and the site's own components. */
export function registryFor(documents: SiteDocuments): Registry {
  return siteRegistry(BUILTIN_COMPONENTS, documents.components).registry;
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

export function DcmsApp({ documents, registry }: { documents: SiteDocuments; registry?: Registry }) {
  const router = useMemo(
    () => createBrowserRouter(siteRoutes(documents, registry ?? registryFor(documents))),
    [documents, registry],
  );
  useEffect(() => {
    for (const problem of documents.problems) console.error(`dcms: ${problem}`);
  }, [documents]);
  return (
    <SiteContext.Provider value={{ app: documents.app }}>
      <SiteStyles theme={documents.theme} />
      <RouterProvider router={router} />
    </SiteContext.Provider>
  );
}

/** The shell around every route: the app's own `shell` tree, whose outlet the page renders into. */
function Shell({ app, registry }: { app: App; registry: Registry }) {
  return app.shell ? <RenderNode node={app.shell} registry={registry} /> : <Outlet />;
}

function RoutePage({ page, app, registry }: { page: Page | undefined; app: App; registry: Registry }) {
  const params = useParams();
  useEffect(() => {
    if (page) applyHead(document, app, page);
  }, [app, page, params]);
  if (!page) return <NotFound />;
  return <RenderNode node={page.root} registry={registry} />;
}

function NotFound() {
  return (
    <main className="dcms-page dcms-not-found">
      <h1 className="dcms-heading dcms-heading-2">Page not found</h1>
    </main>
  );
}
