import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { appSchema, pageSchema, type App, type Page } from './document';
import { headHtml, pageHead } from './head';

/**
 * The Vite plugin a Mode D site's `vite.config.ts` loads. Node-only: never imported by the app.
 *
 * A single-page app ships one `index.html`, so a crawler or a link preview — neither of which
 * runs JavaScript — sees the same title for every address. This writes a copy of `index.html`
 * per route with that route's head tags, named the way site-host looks pages up:
 *
 * - `/` → `index.html`
 * - `/about/team` → `about_team.html`
 * - `/events/:slug` → `events_@.html` (site-host wildcards only the last segment)
 *
 * Any other address still gets `index.html` by site-host's fallback, and the app sets the head
 * itself once it runs. Nothing here changes what the visitor's browser renders.
 */

/** The file site-host serves for a route, or null when it can only be reached by the fallback. */
export function routeFileName(path: string): string | null {
  const segments = path.split('/').filter(Boolean);
  if (segments.length === 0) return 'index.html';
  const params = segments.map((s) => s.startsWith(':'));
  if (params.slice(0, -1).some(Boolean)) return null;
  return `${segments.map((s, i) => (params[i] ? '@' : s)).join('_')}.html`;
}

/** `index.html` with this page's head in place of the template's `<title>`. */
export function withHead(indexHtml: string, app: App, page: Page): string {
  const tags = headHtml(pageHead(app, page));
  return /<title>[\s\S]*?<\/title>/.test(indexHtml)
    ? indexHtml.replace(/<title>[\s\S]*?<\/title>/, tags)
    : indexHtml.replace('</head>', `    ${tags}\n  </head>`);
}

interface OutputAsset {
  type: 'asset';
  fileName: string;
  source: string | Uint8Array;
}

export function dcmsRoutes(options: { root?: string } = {}) {
  return {
    name: 'dcms-routes',
    apply: 'build' as const,
    enforce: 'post' as const,
    generateBundle(
      this: { emitFile(file: { type: 'asset'; fileName: string; source: string }): void },
      _options: unknown,
      bundle: Record<string, OutputAsset | { type: 'chunk' }>,
    ) {
      const root = options.root ?? process.cwd();
      const index = bundle['index.html'];
      if (!index || index.type !== 'asset') return;
      const indexHtml = typeof index.source === 'string' ? index.source : new TextDecoder().decode(index.source);

      const app = appSchema.parse(JSON.parse(readFileSync(join(root, 'dcms/app.json'), 'utf8')));
      const pages = new Map<string, Page>();
      for (const file of readdirSync(join(root, 'dcms/pages'))) {
        if (!file.endsWith('.json')) continue;
        const page = pageSchema.safeParse(JSON.parse(readFileSync(join(root, 'dcms/pages', file), 'utf8')));
        if (page.success) pages.set(page.data.id, page.data);
      }

      for (const route of app.routes) {
        const page = pages.get(route.page);
        const fileName = routeFileName(route.path);
        if (!page || !fileName) continue;
        const html = withHead(indexHtml, app, page);
        if (fileName === 'index.html') index.source = html;
        else this.emitFile({ type: 'asset', fileName, source: html });
      }
    },
  };
}
