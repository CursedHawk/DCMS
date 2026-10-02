import {
  APP_JSON,
  DOC_ID,
  appSchema,
  componentFileOf,
  componentPath,
  pageIdFromPath,
  pagePath,
  pageSchema,
  tenantComponentSchema,
  tenantType,
  walk,
  type App,
  type Node,
  type Page,
  type TenantComponentDoc,
} from '@dcms/site-runtime';
import { useVfs } from '../site-source/vfs';
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

// ---------------------------------------------------------------------------
// The site's own components (P3)
// ---------------------------------------------------------------------------

export interface ComponentInfo {
  name: string;
  /** Every version on disk, ascending. */
  versions: number[];
  latest: TenantComponentDoc;
}

/** Every component in the working draft, by name. Unreadable files are the validator's to report. */
export function listComponents(all: Readonly<Record<string, string>> = files()): ComponentInfo[] {
  const byName = new Map<string, TenantComponentDoc[]>();
  for (const [path, text] of Object.entries(all)) {
    const file = componentFileOf(path);
    if (!file) continue;
    try {
      const doc = tenantComponentSchema.safeParse(JSON.parse(text));
      if (doc.success) byName.set(file.name, [...(byName.get(file.name) ?? []), doc.data]);
    } catch {
      // Reported by checkVisualSite.
    }
  }
  return [...byName.entries()]
    .map(([name, docs]) => {
      docs.sort((a, b) => a.version - b.version);
      return { name, versions: docs.map((d) => d.version), latest: docs.at(-1)! };
    })
    .sort((a, b) => a.latest.label.localeCompare(b.latest.label));
}

export function readComponent(name: string, version: number): TenantComponentDoc | null {
  try {
    const doc = tenantComponentSchema.safeParse(JSON.parse(files()[componentPath(name, version)] ?? ''));
    return doc.success ? doc.data : null;
  } catch {
    return null;
  }
}

export function updateComponent(name: string, version: number, change: (doc: TenantComponentDoc) => TenantComponentDoc): Result {
  const current = readComponent(name, version);
  if (!current) return { ok: false, error: `${componentPath(name, version)} cannot be read.` };
  const next = tenantComponentSchema.safeParse(change(current));
  if (!next.success) return { ok: false, error: next.error.issues[0]?.message ?? 'invalid' };
  useVfs.getState().writeFile(componentPath(name, version), serializeDoc(next.data));
  return { ok: true };
}

/** Every document that can hold component instances: pages, the shell, other components' templates. */
function documentsWithTrees(all: Readonly<Record<string, string>>): { path: string; roots: Node[] }[] {
  const out: { path: string; roots: Node[] }[] = [];
  for (const [path, text] of Object.entries(all)) {
    try {
      if (pageIdFromPath(path)) {
        const page = pageSchema.safeParse(JSON.parse(text));
        if (page.success) out.push({ path, roots: [page.data.root] });
      } else if (path === APP_JSON) {
        const app = appSchema.safeParse(JSON.parse(text));
        if (app.success && app.data.shell) out.push({ path, roots: [app.data.shell] });
      } else if (componentFileOf(path)) {
        const doc = tenantComponentSchema.safeParse(JSON.parse(text));
        if (doc.success) out.push({ path, roots: [doc.data.root] });
      }
    } catch {
      // Unreadable documents hold no instances we could count.
    }
  }
  return out;
}

/** Where a component is used: version → number of instances, across every document. */
export function componentUsage(name: string, all: Readonly<Record<string, string>> = files()): Map<number, number> {
  const type = tenantType(name);
  const latest = listComponents(all).find((c) => c.name === name)?.latest.version;
  const usage = new Map<number, number>();
  for (const { path, roots } of documentsWithTrees(all)) {
    if (componentFileOf(path)?.name === name) continue;
    for (const root of roots) {
      for (const node of walk(root)) {
        if (node.type !== type) continue;
        const v = node.version ?? latest ?? 1;
        usage.set(v, (usage.get(v) ?? 0) + 1);
      }
    }
  }
  return usage;
}

function componentNameFor(label: string, taken: ReadonlySet<string>): string {
  return pageIdFor(label, taken).slice(0, 48);
}

/** A new component, v1 — empty, or made from an existing node tree (a selection on a page). */
export function createComponent(label: string, root?: Node): Result & { name?: string } {
  const taken = new Set(listComponents().map((c) => c.name));
  const name = componentNameFor(label, taken);
  const doc: TenantComponentDoc = {
    schemaVersion: 1,
    name,
    version: 1,
    label,
    props: [],
    slots: [],
    bindings: {},
    slotTargets: {},
    root: root ? freshIds(root) : { id: newNodeId(), type: 'dcms.stack', slots: { default: [] } },
  };
  const parsed = tenantComponentSchema.safeParse(doc);
  if (!parsed.success) return { ok: false, error: parsed.error.issues[0]?.message ?? 'invalid' };
  useVfs.getState().writeFile(componentPath(name, 1), serializeDoc(parsed.data));
  return { ok: true, name };
}

/** A copy of a tree with new ids throughout: a template is its own document, not the page's. */
function freshIds(node: Node): Node {
  const copy = structuredClone(node) as Node;
  const taken = new Set<string>();
  for (const n of walk(copy)) {
    n.id = newNodeId(taken);
    taken.add(n.id);
  }
  return copy;
}

/**
 * The version to edit. A version that pages already use is never changed under them: editing
 * one starts the next version as a copy, and instances move to it when someone updates them.
 */
export function versionToEdit(name: string): { version: number; created: boolean } | null {
  const info = listComponents().find((c) => c.name === name);
  if (!info) return null;
  const latest = info.latest;
  if (!componentUsage(name).get(latest.version)) return { version: latest.version, created: false };
  const next: TenantComponentDoc = { ...latest, version: latest.version + 1 };
  useVfs.getState().writeFile(componentPath(name, next.version), serializeDoc(next));
  return { version: next.version, created: true };
}

/** Delete every version of a component nothing uses. */
export function deleteComponent(name: string): Result {
  const used = [...componentUsage(name).values()].reduce((a, b) => a + b, 0);
  if (used > 0) return { ok: false, error: `It is used ${used} times. Remove those first.` };
  const info = listComponents().find((c) => c.name === name);
  for (const v of info?.versions ?? []) useVfs.getState().deleteFile(componentPath(name, v));
  return { ok: true };
}

/**
 * An instance moved to another version: settings the new version does not have are dropped
 * (and named, so the author knows what changed); slot content is kept under the same slot name.
 */
export function migrateInstance(node: Node, to: TenantComponentDoc): { node: Node; dropped: string[] } {
  const keep = new Set(to.props.map((p) => p.name));
  const props = Object.fromEntries(Object.entries(node.props ?? {}).filter(([k]) => keep.has(k)));
  const dropped = Object.keys(node.props ?? {}).filter((k) => !keep.has(k));
  const next: Node = { ...node, version: to.version, props: Object.keys(props).length ? props : undefined };
  if (!next.props) delete next.props;
  return { node: next, dropped };
}

/** Move every instance, in every document, to the latest version. */
export function updateAllInstances(name: string): { updated: number; dropped: string[] } {
  const info = listComponents().find((c) => c.name === name);
  if (!info) return { updated: 0, dropped: [] };
  const type = tenantType(name);
  let updated = 0;
  const dropped = new Set<string>();
  const rewrite = (node: Node): Node => {
    let next = node;
    if (node.type === type && (node.version ?? info.latest.version) !== info.latest.version) {
      const migrated = migrateInstance(node, info.latest);
      migrated.dropped.forEach((d) => dropped.add(d));
      next = migrated.node;
      updated++;
    }
    if (!next.slots) return next;
    return { ...next, slots: Object.fromEntries(Object.entries(next.slots).map(([k, v]) => [k, v.map(rewrite)])) };
  };

  const all = files();
  for (const [path, text] of Object.entries(all)) {
    if (componentFileOf(path)?.name === name) continue;
    const before = updated;
    try {
      const json = JSON.parse(text);
      if (pageIdFromPath(path) && pageSchema.safeParse(json).success) {
        const page = json as Page;
        const root = rewrite(page.root);
        if (updated > before) useVfs.getState().writeFile(path, serializeDoc({ ...page, root }));
      } else if (path === APP_JSON && appSchema.safeParse(json).success && (json as App).shell) {
        const app = json as App;
        const shell = rewrite(app.shell!);
        if (updated > before) useVfs.getState().writeFile(path, serializeDoc({ ...app, shell }));
      } else if (componentFileOf(path) && tenantComponentSchema.safeParse(json).success) {
        const doc = json as TenantComponentDoc;
        const root = rewrite(doc.root);
        if (updated > before) useVfs.getState().writeFile(path, serializeDoc({ ...doc, root }));
      }
    } catch {
      // Unreadable documents are left alone; the validator reports them.
    }
  }
  return { updated, dropped: [...dropped] };
}

/** The file a canvas target is stored in. */
export function targetPath(target: { kind: 'page'; id: string } | { kind: 'shell' } | { kind: 'component'; name: string; version: number }): string {
  return target.kind === 'page' ? pagePath(target.id) : target.kind === 'component' ? componentPath(target.name, target.version) : APP_JSON;
}

/** Replace one node, by id, wherever it is in a document's tree. */
export function replaceNodeInFile(path: string, nodeId: string, replacement: Node): boolean {
  let found = false;
  const swap = (node: Node): Node => {
    if (node.id === nodeId) {
      found = true;
      return replacement;
    }
    if (!node.slots) return node;
    return { ...node, slots: Object.fromEntries(Object.entries(node.slots).map(([k, v]) => [k, v.map(swap)])) };
  };
  try {
    const json = JSON.parse(files()[path] ?? '');
    if (path === APP_JSON) {
      const app = json as App;
      if (!app.shell) return false;
      const shell = swap(app.shell);
      if (found) useVfs.getState().writeFile(path, serializeDoc({ ...app, shell }));
    } else {
      const doc = json as { root: Node };
      const root = swap(doc.root);
      if (found) useVfs.getState().writeFile(path, serializeDoc({ ...doc, root }));
    }
  } catch {
    return false;
  }
  return found;
}

/**
 * Remove wiring to nodes that are no longer in the template. Deleting the heading a setting was
 * wired to must not leave a component file the schema refuses — the next load would fail.
 */
export function pruneComponent(doc: TenantComponentDoc): TenantComponentDoc {
  const ids = new Set([...walk(doc.root)].map((n) => n.id));
  const bindings = Object.fromEntries(
    Object.entries(doc.bindings).map(([prop, targets]) => [prop, targets.filter((t) => ids.has(t.node))]),
  );
  const slotTargets = Object.fromEntries(Object.entries(doc.slotTargets).filter(([, t]) => ids.has(t.node)));
  return { ...doc, bindings, slotTargets, slots: doc.slots.filter((s) => slotTargets[s.name]) };
}

