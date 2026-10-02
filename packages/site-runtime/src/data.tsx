import { createContext, useContext, useEffect, useState } from 'react';
import { z } from 'zod';
import type { Action } from './actions';
import type { PropDefinition } from './props';

/**
 * Content from the tenant's plugins, for components to show (ADR 0020, P4).
 *
 * The runtime speaks the delivery API itself — `/api/{instance}/{contentType}` — rather than the
 * site's generated client, because the same code also runs on the builder's canvas, where there is
 * no generated client to import. What differs between the two is only *how* a request is made:
 * on the site it is same-origin `fetch`; in the builder the admin provides a client that goes
 * through its authenticated preview proxy (real published content, sandboxed writes).
 *
 * Components never see the client: they receive bound values as props, and a collection or a
 * detail page supplies the item those values are read from.
 */

/** Which content: a plugin instance (its slug) and one of its content types. */
export const sourceSchema = z.strictObject({
  instance: z.string().regex(/^[a-z0-9][a-z0-9-]{0,63}$/, 'must be a plugin instance slug'),
  contentType: z.string().regex(/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/, 'must be a content type name'),
});
export type Source = z.infer<typeof sourceSchema>;

/** One published item, as the delivery API returns it. */
export interface ContentItem {
  id: string;
  slug: string;
  contentType?: string;
  publishedAt?: string;
  data: Record<string, unknown>;
}

export interface ItemList {
  items: ContentItem[];
  totalCount: number;
}

export interface DataClient {
  get(path: string): Promise<unknown>;
  post(path: string, body: unknown): Promise<unknown>;
}

/** Same-origin `fetch`: on a published site `/api/*` is served by site-host's proxy. */
export const fetchDataClient: DataClient = {
  async get(path) {
    const res = await fetch(path, { headers: { Accept: 'application/json' } });
    if (!res.ok) throw new DataError(res.status, path);
    return res.json();
  },
  async post(path, body) {
    const res = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    if (!res.ok) throw new DataError(res.status, path, await res.json().catch(() => undefined));
    return res.status === 204 ? undefined : res.json().catch(() => undefined);
  },
};

export class DataError extends Error {
  constructor(
    readonly status: number,
    readonly path: string,
    readonly body?: unknown,
  ) {
    super(`${path} failed: ${status}`);
  }
}

export const DataClientContext = createContext<DataClient>(fetchDataClient);

/**
 * The item a node's bindings read from: the current item of a collection, or the item a detail
 * page shows. `index`/`count` let a template say "first", "3 of 8".
 */
export interface ItemScope {
  item: ContentItem;
  index: number;
  count: number;
}
export const ItemContext = createContext<ItemScope | null>(null);

export function useItem(): ItemScope | null {
  return useContext(ItemContext);
}

export function listPath(source: Source, options: { limit?: number; tag?: string; page?: number } = {}): string {
  const query = new URLSearchParams();
  if (options.page) query.set('page', String(options.page));
  if (options.limit) query.set('pageSize', String(options.limit));
  if (options.tag) query.set('tag', options.tag);
  const qs = query.toString();
  return `/api/${source.instance}/${encodeURIComponent(source.contentType)}${qs ? `?${qs}` : ''}`;
}

export function itemPath(source: Source, slug: string): string {
  return `/api/${source.instance}/${encodeURIComponent(source.contentType)}/${encodeURIComponent(slug)}`;
}

/**
 * One request per path, however many components ask: a page with three collections over the
 * same content makes one call. Results are kept for the life of the page — published content
 * does not change under a visitor often enough to be worth more.
 */
const cache = new WeakMap<DataClient, Map<string, Promise<unknown>>>();

function cached(client: DataClient, path: string): Promise<unknown> {
  let byPath = cache.get(client);
  if (!byPath) cache.set(client, (byPath = new Map()));
  let pending = byPath.get(path);
  if (!pending) {
    pending = client.get(path);
    byPath.set(path, pending);
    // A failure is not remembered: the next render may try again.
    pending.catch(() => byPath!.delete(path));
  }
  return pending;
}

/** Forget what was fetched — the builder calls this when the author asks for fresh content. */
export function clearDataCache(client: DataClient): void {
  cache.delete(client);
}

export type Loadable<T> = { state: 'loading' } | { state: 'error'; error: Error } | { state: 'ready'; value: T };

/** Fetch through the client in context. `path === null` means "not configured": nothing happens. */
export function useData<T>(path: string | null, parse: (json: unknown) => T): Loadable<T> {
  const client = useContext(DataClientContext);
  const [state, setState] = useState<Loadable<T>>({ state: 'loading' });
  useEffect(() => {
    if (!path) return;
    let live = true;
    setState({ state: 'loading' });
    cached(client, path).then(
      (json) => live && setState({ state: 'ready', value: parse(json) }),
      (error: Error) => live && setState({ state: 'error', error }),
    );
    return () => {
      live = false;
    };
    // `parse` is a module-level function at every call site.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, path]);
  return path ? state : { state: 'error', error: new Error('not configured') };
}

export function parseItemList(json: unknown): ItemList {
  const body = (json ?? {}) as { items?: unknown; totalCount?: unknown };
  const items = Array.isArray(body.items) ? (body.items as ContentItem[]).filter(isItem) : [];
  return { items, totalCount: typeof body.totalCount === 'number' ? body.totalCount : items.length };
}

export function parseItem(json: unknown): ContentItem {
  if (!isItem(json)) throw new Error('not a content item');
  return json;
}

function isItem(value: unknown): value is ContentItem {
  const v = value as ContentItem | null;
  return !!v && typeof v === 'object' && typeof v.slug === 'string' && typeof v.data === 'object' && v.data !== null;
}

/**
 * A field of the item in scope. Paths are those the admin's field lists use: `title`,
 * `values.colour` (a tenant-defined field), and the meta fields `#slug`, `#id`, `#publishedAt`,
 * `#index`, `#number` (from 1) and `#count`.
 */
export function readField(scope: ItemScope, path: string): unknown {
  switch (path) {
    case '#slug':
      return scope.item.slug;
    case '#id':
      return scope.item.id;
    case '#publishedAt':
      return scope.item.publishedAt;
    case '#index':
      return scope.index;
    case '#number':
      return scope.index + 1;
    case '#count':
      return scope.count;
  }
  let value: unknown = scope.item.data;
  for (const key of path.split('.')) {
    if (value === null || typeof value !== 'object') return undefined;
    value = (value as Record<string, unknown>)[key];
  }
  return value;
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A media field as an image URL: a stored asset id becomes its published delivery URL. */
export function mediaUrl(value: unknown): string | undefined {
  if (typeof value === 'string') {
    if (GUID.test(value)) return `/api/media/${value}/original`;
    if (value.startsWith('/') || /^https?:\/\//.test(value)) return value;
    return undefined;
  }
  // Some plugins store a media field as `{ id, url }`.
  if (value && typeof value === 'object') {
    const v = value as { url?: unknown; id?: unknown };
    return mediaUrl(v.url) ?? mediaUrl(v.id);
  }
  return undefined;
}

/** A field's value converted to what a prop of this kind holds — or nothing, if it cannot be. */
export function coerce(prop: PropDefinition, value: unknown): unknown {
  if (value === undefined || value === null) return undefined;
  switch (prop.kind) {
    case 'media':
      return mediaUrl(value);
    case 'number': {
      const n = typeof value === 'number' ? value : Number(value);
      return Number.isFinite(n) ? n : undefined;
    }
    case 'boolean':
      return Boolean(value);
    case 'text':
    case 'richText':
    case 'url':
    case 'date':
      if (Array.isArray(value)) return value.join(', ');
      return typeof value === 'object' ? undefined : String(value);
    default:
      return undefined;
  }
}

/** Which field kinds a prop of each kind may be bound to — the same table the field picker uses. */
export const BINDABLE: Partial<Record<PropDefinition['kind'], readonly string[]>> = {
  text: ['text', 'richText', 'date', 'number', 'url', 'tags'],
  richText: ['richText', 'text'],
  media: ['media'],
  url: ['url', 'text'],
  date: ['date'],
  number: ['number'],
  boolean: ['boolean'],
};

/** A node's props with its bindings read from the item in scope. Unbound props pass through. */
export function bindProps(
  props: Record<string, unknown>,
  bind: Readonly<Record<string, string>> | undefined,
  definitions: readonly PropDefinition[],
  scope: ItemScope | null,
): Record<string, unknown> {
  if (!bind || !scope) return props;
  const out = { ...props };
  for (const def of definitions) {
    const path = bind[def.name];
    if (path === undefined) continue;
    const value = coerce(def, readField(scope, path));
    if (value !== undefined) out[def.name] = value;
  }
  return out;
}

/**
 * A navigate action inside an item's scope: `:slug` and `:id` in the target are that item's —
 * which is how a card in a list links to the detail page of the item it shows.
 */
export function bindAction(action: Action | undefined, scope: ItemScope | null): Action | undefined {
  if (!action || action.type !== 'navigate' || !scope || !action.to.includes(':')) return action;
  const to = action.to
    .replace(/:slug\b/g, encodeURIComponent(scope.item.slug))
    .replace(/:id\b/g, encodeURIComponent(scope.item.id));
  return { ...action, to };
}
