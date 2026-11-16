// @vitest-environment node
import { cpSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterAll, describe, expect, it } from 'vitest';

/*
 * site-builder's Mode B page list (#343): src/Services/Dcms.SiteBuilder/Runtime/sitemap-routes.mjs.
 * Tested here because this package has what a Mode B site builds with — vite, react, react-router —
 * and the templates the script has to read.
 */
const SCRIPT = join(__dirname, '../../../src/Services/Dcms.SiteBuilder/Runtime/sitemap-routes.mjs');
const PACKAGE = join(__dirname, '..');

type Page = { path: string; instance?: string; contentType?: string };
type Script = {
  pagesFromRoutes: (routes: unknown[], collections?: unknown[]) => Page[];
  listPages: (root: string) => Promise<{ pages: Page[]; note: string }>;
};
const load = (file = SCRIPT) => import(/* @vite-ignore */ file) as Promise<Script>;

describe('pagesFromRoutes', () => {
  it('lists static routes through layouts, nesting and index routes', async () => {
    const { pagesFromRoutes } = await load();
    expect(pagesFromRoutes([
      {
        children: [
          { index: true },
          { path: 'about', children: [{ index: true }, { path: 'team' }] },
          { path: '/contact/' },
          { path: '*' },
          { path: 'docs/*' },
          { path: 'lang/:locale?' },
          { path: 'users/:id' },
        ],
      },
    ])).toEqual([{ path: '/' }, { path: '/about' }, { path: '/about/team' }, { path: '/contact' }]);
  });

  it('follows each route\'s handle.sitemap', async () => {
    const { pagesFromRoutes } = await load();
    expect(pagesFromRoutes([
      { path: '/', children: [
        { path: 'account', handle: { sitemap: false }, children: [{ path: 'settings' }] },
        { path: 'blog/:slug', handle: { sitemap: { instance: 'blog', contentType: 'post' } } },
        { path: 'shop/:a/:b', handle: { sitemap: { instance: 'shop', contentType: 'product' } } },
        { path: 'press', handle: { sitemap: [{ path: '/press/2026' }, { path: 'press/news/:slug', instance: 'news', contentType: 'post' }] } },
      ] },
    ])).toEqual([
      { path: '/' },
      { path: '/blog/:slug', instance: 'blog', contentType: 'post' },
      { path: '/press/2026' },
      { path: '/press/news/:slug', instance: 'news', contentType: 'post' },
    ]);
  });

  it('expands the starter template\'s generic routes over every collection', async () => {
    const { pagesFromRoutes } = await load();
    const collections = [{ instance: 'news', contentType: 'post' }, { instance: 'events', contentType: 'event' }];
    expect(pagesFromRoutes([
      { children: [{ index: true }, { path: ':instance/:contentType' }, { path: ':instance/:contentType/:slug' }] },
    ], collections)).toEqual([
      { path: '/' },
      { path: '/news/post' },
      { path: '/events/event' },
      { path: '/news/post/:slug', instance: 'news', contentType: 'post' },
      { path: '/events/event/:slug', instance: 'events', contentType: 'event' },
    ]);
  });
});

describe('listPages', () => {
  const dirs: string[] = [];
  afterAll(() => dirs.forEach((dir) => rmSync(dir, { recursive: true, force: true })));

  /** A project inside this package, so its imports resolve to this package's node_modules. */
  function project(layers: string[], files: Record<string, string> = {}) {
    const dir = mkdtempSync(join(PACKAGE, '.sitemap-test-'));
    dirs.push(dir);
    for (const layer of layers) cpSync(join(PACKAGE, layer), dir, { recursive: true });
    // The template's vite.config.ts wants @vitejs/plugin-react, which a site installs and this
    // package does not; Vite compiles TSX on its own.
    rmSync(join(dir, 'vite.config.ts'), { force: true });
    for (const [path, content] of Object.entries(files)) writeFileSync(join(dir, path), content);
    cpSync(SCRIPT, join(dir, '.dcms-sitemap.mjs'));
    return dir;
  }

  it('reads the content template as a site builds it: home, every collection and its items', async () => {
    const dir = project(['shared', 'templates/content']);
    const { listPages } = await load(join(dir, '.dcms-sitemap.mjs'));

    const { pages, note } = await listPages(dir);

    expect(pages).toEqual([
      { path: '/' },
      { path: '/news/post' },
      { path: '/news/post/:slug', instance: 'news', contentType: 'post' },
    ]);
    expect(note).toBe('3 page(s) from src/routes.tsx.');
  }, 60_000);

  it('lists only / for a site without routes', async () => {
    const dir = project(['shared', 'templates/blank']);
    const { listPages } = await load(join(dir, '.dcms-sitemap.mjs'));

    expect(await listPages(dir)).toEqual(expect.objectContaining({ pages: [{ path: '/' }] }));
  });

  it('fails without listing anything when the routes cannot load outside a browser', async () => {
    const dir = project(['shared', 'templates/blank'], {
      'src/routes.tsx': 'window.scrollTo(0, 0);\nexport const routes = [];\n',
    });
    const { listPages } = await load(join(dir, '.dcms-sitemap.mjs'));

    await expect(listPages(dir)).rejects.toThrow(/window/);
  }, 60_000);
});
