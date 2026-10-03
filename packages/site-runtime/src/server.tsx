import { renderToString } from 'react-dom/server';
import { RouterProvider, createMemoryRouter } from 'react-router';
import { SiteStyles, registryFor, siteRoutes, type SiteDocuments } from './app';
import { SiteContext } from './site';
import { routeFileName } from './vite';

/**
 * Prerendering (backlog #125): each route without parameters, rendered at build time into its
 * own HTML file, so a visitor — and a crawler — gets the page's text before any script runs.
 * The browser then hydrates it (src/main.tsx).
 *
 * Node-only, like vite.ts: the template's `src/entry-server.tsx` imports it for the SSR build.
 *
 * Plugin content is not fetched here — the build cannot reach the API — so collections and
 * detail pages prerender in their loading state, exactly as the browser's first render does;
 * the content arrives client-side as before. Routes with `:params` are not prerendered.
 */

/** One route's markup: the same tree DcmsApp renders, under a memory router at `path`. */
export function renderRoute(documents: SiteDocuments, path: string): string {
  const router = createMemoryRouter(siteRoutes(documents, registryFor(documents)), { initialEntries: [path] });
  return renderToString(
    <SiteContext.Provider value={{ app: documents.app }}>
      <SiteStyles theme={documents.theme} />
      <RouterProvider router={router} />
    </SiteContext.Provider>,
  );
}

/**
 * Every prerenderable route's file and markup. A route that throws is left out — its HTML file
 * keeps the empty root and renders in the browser as before — and named on the console.
 */
export function prerender(documents: SiteDocuments): { path: string; fileName: string; html: string }[] {
  const out: { path: string; fileName: string; html: string }[] = [];
  for (const route of documents.app.routes) {
    const fileName = routeFileName(route.path);
    if (!fileName || route.path.includes(':')) continue;
    try {
      out.push({ path: route.path, fileName, html: renderRoute(documents, route.path) });
    } catch (error) {
      console.warn(`dcms: ${route.path} was not prerendered: ${(error as Error).message}`);
    }
  }
  return out;
}
