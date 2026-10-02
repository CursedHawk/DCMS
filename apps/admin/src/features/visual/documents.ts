import {
  APP_JSON,
  DOC_ID,
  appSchema,
  pagePath,
  pageSchema,
  type App,
  type Node,
  type Page,
} from '@dcms/site-runtime';
import { useVfs } from '../site-source';
import { newNodeId } from './canvas/tree';
import { serializeDoc } from './starter';

/**
 * Edits to the documents a Mode D site is made of, outside the canvas: routes, page files,
 * menus, the shell, SEO.
 *
 * Every write goes through the working draft (`useVfs`), so it autosaves, appears in source
 * control and can be reverted like any edit; and every one is validated against the same
 * schema the site is loaded with, so the panels cannot save an `app.json` the builder — or the
 * published site — would then refuse.
 */

export type Result = { ok: true } | { ok: false; error: string };

function files(): Readonly<Record<string, string>> {
  return useVfs.getState().files;
}

export function readApp(): App | null {
  try {
    const parsed = appSchema.safeParse(JSON.parse(files()[APP_JSON] ?? ''));
    return parsed.success ? parsed.data : null;
  } catch {
    return null;
  }
}

export function readPage(id: string): Page | null {
  try {
    const parsed = pageSchema.safeParse(JSON.parse(files()[pagePath(id)] ?? ''));
    return parsed.success ? parsed.data : null;
  } catch {
    return null;
  }
}

/** Apply a change to `app.json`, refusing one the schema would reject — with its reason. */
export function updateApp(change: (app: App) => App): Result {
  const current = readApp();
  if (!current) return { ok: false, error: 'dcms/app.json cannot be read. Fix it in the code view first.' };
  const next = appSchema.safeParse(change(current));
  if (!next.success) {
    const issue = next.error.issues[0];
    return { ok: false, error: issue?.message ?? 'invalid' };
  }
  useVfs.getState().writeFile(APP_JSON, serializeDoc(next.data));
  return { ok: true };
}

export function updatePage(id: string, change: (page: Page) => Page): Result {
  const current = readPage(id);
  if (!current) return { ok: false, error: `${pagePath(id)} cannot be read.` };
  const next = pageSchema.safeParse(change(current));
  if (!next.success) return { ok: false, error: next.error.issues[0]?.message ?? 'invalid' };
  useVfs.getState().writeFile(pagePath(id), serializeDoc(next.data));
  return { ok: true };
}

/** `About us` → `about-us`; unique among the existing page ids. */
export function pageIdFor(title: string, taken: ReadonlySet<string>): string {
  const base =
    title
      .normalize('NFKD')
      .replace(/[̀-ͯ]/g, '')
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 48) || 'page';
  let id = base;
  for (let n = 2; taken.has(id); n++) id = `${base}-${n}`;
  return DOC_ID.test(id) ? id : `page-${Date.now()}`;
}

/** A new page, its route, and (optionally) an entry in the main menu — or why not. */
export function createPage(title: string, path: string, addToMenu: boolean): Result & { id?: string } {
  const app = readApp();
  if (!app) return { ok: false, error: 'dcms/app.json cannot be read.' };
  const taken = new Set(Object.keys(files()).map((p) => /^dcms\/pages\/(.+)\.json$/.exec(p)?.[1]).filter(Boolean) as string[]);
  const id = pageIdFor(title, taken);

  const routeIds = new Set(app.routes.map((r) => r.id));
  let routeId = id;
  for (let n = 2; routeIds.has(routeId); n++) routeId = `${id}-${n}`;

  const result = updateApp((a) => ({
    ...a,
    routes: [...a.routes, { id: routeId, path, page: id }],
    navigation: addToMenu
      ? { ...a.navigation, main: [...(a.navigation?.main ?? []), { label: title, to: path }] }
      : a.navigation,
  }));
  if (!result.ok) return result;

  const page: Page = {
    schemaVersion: 1,
    id,
    title,
    root: { id: newNodeId(), type: 'dcms.page', slots: { default: [] } },
  };
  useVfs.getState().writeFile(pagePath(id), serializeDoc(page));
  return { ok: true, id };
}

/** Remove a page, every route to it, and every menu entry pointing at those routes. */
export function deletePage(id: string): Result {
  const app = readApp();
  if (!app) return { ok: false, error: 'dcms/app.json cannot be read.' };
  const removed = app.routes.filter((r) => r.page === id);
  if (removed.some((r) => r.path === '/')) return { ok: false, error: 'The home page cannot be deleted. Make another page the home page first.' };
  const paths = new Set(removed.map((r) => r.path));
  const result = updateApp((a) => ({
    ...a,
    routes: a.routes.filter((r) => r.page !== id),
    navigation: a.navigation
      ? Object.fromEntries(Object.entries(a.navigation).map(([menu, items]) => [menu, items.filter((i) => !paths.has(i.to))]))
      : undefined,
  }));
  if (!result.ok) return result;
  useVfs.getState().deleteFile(pagePath(id));
  return { ok: true };
}

/** Change a route's address, and the menu entries that pointed at the old one. */
export function setRoutePath(routeId: string, path: string): Result {
  const app = readApp();
  const route = app?.routes.find((r) => r.id === routeId);
  if (!app || !route) return { ok: false, error: 'No such route.' };
  const old = route.path;
  return updateApp((a) => ({
    ...a,
    routes: a.routes.map((r) => (r.id === routeId ? { ...r, path } : r)),
    navigation: a.navigation
      ? Object.fromEntries(
          Object.entries(a.navigation).map(([menu, items]) => [menu, items.map((i) => (i.to === old ? { ...i, to: path } : i))]),
        )
      : undefined,
  }));
}

/** Whether the main menu has an entry for this path, and adding or removing it. */
export function inMainMenu(app: App, path: string): boolean {
  return (app.navigation?.main ?? []).some((i) => i.to === path);
}

export function toggleMainMenu(path: string, label: string): Result {
  return updateApp((a) => {
    const main = a.navigation?.main ?? [];
    const next = main.some((i) => i.to === path) ? main.filter((i) => i.to !== path) : [...main, { label, to: path }];
    return { ...a, navigation: { ...a.navigation, main: next } };
  });
}

/** The app shell a site gets the first time someone opens it: the main menu, then the page. */
export function defaultShell(): Node {
  return {
    id: 'shell',
    type: 'dcms.page',
    slots: {
      default: [
        {
          id: 'shell-header',
          type: 'dcms.section',
          props: { spacing: 'sm', width: 'normal' },
          slots: { default: [{ id: 'shell-nav', type: 'dcms.nav', props: { menu: 'main' } }] },
        },
        { id: 'shell-outlet', type: 'dcms.outlet' },
      ],
    },
  };
}
