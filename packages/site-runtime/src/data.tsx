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
export const pluginSourceSchema = z.strictObject({
  instance: z.string().regex(/^[a-z0-9][a-z0-9-]{0,63}$/, 'must be a plugin instance slug'),
  contentType: z.string().regex(/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/, 'must be a content type name'),
});

const DOT_PATH = /^[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*$/;

/**
 * An external API connection's operation (backlog #124): `/api/connections/{connection}{operation}`
 * serves its last synced response. `items` is where the list sits in it (`data.events`; absent:
 * the response itself), `id` and `slug` which fields of an item are those (default `id`, `slug`).
 */
export const connectionSourceSchema = z.strictObject({
  connection: z.string().regex(/^[a-z0-9][a-z0-9-]{0,47}$/, 'must be a connection slug'),
  operation: z.string().max(512).regex(/^\/(?!\/)[^\s#]*$/, 'must be a path starting with /'),
  items: z.string().regex(DOT_PATH, 'must be a field path like data.items').optional(),
  id: z.string().regex(DOT_PATH).optional(),
  slug: z.string().regex(DOT_PATH).optional(),
});

const API_NAME = /^[A-Za-z][A-Za-z0-9_]{0,63}$/;

/**
 * A Dynamic Apps table: the app's instance slug and the table's API name, read through the app's
 * public API (`/api/{app}/data/{table}`) — what the site may read of it, as the app's access rules
 * say. Not a plugin content type: an app serves records, not published items, and content
 * delivery owns `/api/{slug}/{contentType}`. `sort` is a field (`-` first for descending);
 * `expand` lists lookups to fetch whole, so a binding can read `account.name` rather than an id.
 * `related` names a relationship of `table`: the records related to the one in scope — an
 * account's deals, on the account's detail page — at `/api/{app}/data/{table}/{id}/{related}`.
 */
export const appSourceSchema = z.strictObject({
  app: z.string().regex(/^[a-z0-9][a-z0-9-]{0,63}$/, 'must be a Dynamic Apps instance slug'),
  table: z.string().regex(API_NAME, "must be a table's API name"),
  sort: z.string().regex(/^-?[A-Za-z][A-Za-z0-9_]{0,63}$/, 'must be a field API name, - first for descending').optional(),
  expand: z.array(z.string().regex(API_NAME, 'must be a lookup field API name')).max(10).optional(),
  related: z.string().regex(API_NAME, 'must be a relationship API name').optional(),
});

export const sourceSchema = z.union([pluginSourceSchema, connectionSourceSchema, appSourceSchema]);
export type Source = z.infer<typeof sourceSchema>;
export type ConnectionSource = z.infer<typeof connectionSourceSchema>;
export type AppSource = z.infer<typeof appSourceSchema>;

export function isConnectionSource(source: Source): source is ConnectionSource {
  return 'connection' in source;
}

export function isAppSource(source: Source): source is AppSource {
  return 'app' in source;
}

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
  /** Changes beyond a POST — a data table's saved views, a record form's edit. Absent where nothing may be changed (the builder). */
  send?(method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', path: string, body?: unknown): Promise<unknown>;
}

/** Same-origin `fetch`: on a published site `/api/*` is served by site-host's proxy. */
/** A client for the delivery API at `base` (empty: the site's own origin, which proxies /api). */
export function createFetchDataClient(base = ''): DataClient {
  const send: NonNullable<DataClient['send']> = async (method, path, body) => {
    const res = await fetch(base + path, {
      method,
      // One key per write: a double click, or a retry after a dropped connection, is the same
      // write to content-api rather than a second record (ADR 0024).
      headers: {
        'Idempotency-Key': crypto.randomUUID(),
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!res.ok) throw new DataError(res.status, path, await res.json().catch(() => undefined));
    return res.status === 204 ? undefined : res.json().catch(() => undefined);
  };
  return {
    async get(path) {
      const res = await fetch(base + path, { headers: { Accept: 'application/json' } });
      if (!res.ok) throw new DataError(res.status, path);
      return res.json();
    },
    post: (path, body) => send('POST', path, body),
    send,
  };
}

export const fetchDataClient: DataClient = createFetchDataClient();

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
  if (isConnectionSource(source)) return `/api/connections/${source.connection}${source.operation}`;
  if (isAppSource(source)) {
    const query = new URLSearchParams();
    if (options.page) query.set('page', String(options.page));
    if (options.limit) query.set('pageSize', String(options.limit));
    if (source.sort) query.set('sort', source.sort);
    if (source.expand?.length) query.set('expand', source.expand.join(','));
    const qs = query.toString();
    return `/api/${source.app}/data/${encodeURIComponent(source.table)}${qs ? `?${qs}` : ''}`;
  }
  const query = new URLSearchParams();
  if (options.page) query.set('page', String(options.page));
  if (options.limit) query.set('pageSize', String(options.limit));
  if (options.tag) query.set('tag', options.tag);
  const qs = query.toString();
  return `/api/${source.instance}/${encodeURIComponent(source.contentType)}${qs ? `?${qs}` : ''}`;
}

/** An app source's related records for the record `id` — the list a `related` source reads. */
export function relatedPath(source: AppSource, id: string, options: { limit?: number; page?: number } = {}): string {
  const query = new URLSearchParams();
  if (options.page) query.set('page', String(options.page));
  if (options.limit) query.set('pageSize', String(options.limit));
  const qs = query.toString();
  return `/api/${source.app}/data/${encodeURIComponent(source.table)}/${encodeURIComponent(id)}/${encodeURIComponent(source.related ?? '')}${qs ? `?${qs}` : ''}`;
}

export function itemPath(source: Source, slug: string): string {
  // A connection serves lists only: a detail page finds its item in the list.
  if (isConnectionSource(source)) return listPath(source);
  if (isAppSource(source)) {
    const expand = source.expand?.length ? `?expand=${encodeURIComponent(source.expand.join(','))}` : '';
    return `/api/${source.app}/data/${encodeURIComponent(source.table)}/${encodeURIComponent(slug)}${expand}`;
  }
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
    // Parsed inside the chain: a response that does not parse is an error state (a detail
    // page's 404), not an unhandled rejection.
    cached(client, path)
      .then((json) => parse(json))
      .then(
        (value) => live && setState({ state: 'ready', value }),
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

/**
 * A connection's response as items: the array at `items`, each object an item whose id and slug
 * are read from the named fields (a slug falls back to the id). Anything not an object is skipped.
 */
function connectionItems(source: ConnectionSource, json: unknown): ContentItem[] {
  const at = (value: unknown, path: string | undefined) =>
    path ? path.split('.').reduce<unknown>((v, k) => (v && typeof v === 'object' ? (v as Record<string, unknown>)[k] : undefined), value) : value;
  const list = at(json, source.items);
  if (!Array.isArray(list)) return [];
  return list.flatMap((value, index) => {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return [];
    const id = at(value, source.id ?? 'id');
    const slug = at(value, source.slug ?? 'slug') ?? id;
    return [{ id: String(id ?? index), slug: String(slug ?? index), data: value as Record<string, unknown> }];
  });
}

/**
 * A Dynamic Apps record as an item: it has an id and no slug (a detail page's `:id`/`:slug` are both
 * the id), and its fields — `name`, `stage`, an expanded `account` — are the item's data as they come.
 */
function recordItem(value: unknown): ContentItem | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const record = value as Record<string, unknown>;
  if (typeof record.id !== 'string') return null;
  return {
    id: record.id,
    slug: record.id,
    publishedAt: typeof record.updated_at === 'string' ? record.updated_at : undefined,
    data: record,
  };
}

/** How a list response reads for this source, at most `limit` items. */
export function listParser(source: Source, limit?: number): (json: unknown) => ItemList {
  if (isAppSource(source)) {
    return (json) => {
      const body = (json ?? {}) as { items?: unknown; total?: unknown };
      const items = (Array.isArray(body.items) ? body.items : []).map(recordItem).filter((i): i is ContentItem => !!i);
      return { items, totalCount: typeof body.total === 'number' ? body.total : items.length };
    };
  }
  if (!isConnectionSource(source)) return parseItemList;
  return (json) => {
    const all = connectionItems(source, json);
    return { items: limit ? all.slice(0, limit) : all, totalCount: all.length };
  };
}

/** How an item response reads for this source; for a connection, the list's item with this slug. */
export function itemParser(source: Source, slug: string): (json: unknown) => ContentItem {
  if (isAppSource(source)) {
    return (json) => {
      const item = recordItem(json);
      if (!item) throw new Error('not a record');
      return item;
    };
  }
  if (!isConnectionSource(source)) return parseItem;
  return (json) => {
    const found = connectionItems(source, json).find((item) => item.slug === slug);
    if (!found) throw new Error('no such item');
    return found;
  };
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

/**
 * A media field as an image URL: a stored asset id becomes its published delivery URL. A list
 * of media (a gallery's images, an event's photos) is shown by its first.
 */
export function mediaUrl(value: unknown): string | undefined {
  if (Array.isArray(value)) return mediaUrl(value[0]);
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

/** A list of media (a gallery's pictures, an event's photos) as URLs; one item is a list of one. */
export function mediaList(value: unknown): string[] {
  return (Array.isArray(value) ? value : [value]).map((v) => mediaUrl(v)).filter((u): u is string => !!u);
}

/** The widths of the WebP ladder the media worker makes of every picture (WebpLadderGenerator). */
export const WEBP_WIDTHS = [320, 640, 1280, 1920] as const;
export type WebpWidth = (typeof WEBP_WIDTHS)[number];
const LIBRARY_ORIGINAL = /^\/api\/media\/([0-9a-f-]{36})\/original$/i;

/**
 * A picture from the media library as browsers should load it: WebP, at the width the layout
 * needs (`srcSet`), `width` where there is no choice (a background, a poster). The server answers a
 * width an asset was not made at with its nearest, and the original while it has none, so every
 * candidate loads. Anything else — an explicitly chosen variant, an external address, a site
 * file — is used as given.
 */
export function picture(value: unknown, width: WebpWidth = 1280): { src: string; srcSet?: string } | undefined {
  const url = mediaUrl(value);
  if (!url) return undefined;
  const id = LIBRARY_ORIGINAL.exec(url)?.[1];
  if (!id) return { src: url };
  return { src: `/api/media/${id}/webp-${width}`, srcSet: WEBP_WIDTHS.map((w) => `/api/media/${id}/webp-${w} ${w}w`).join(', ') };
}

/**
 * How wide a picture is drawn, for the browser to pick from its `srcSet`: the whole screen unless
 * something narrower says otherwise — a gallery, by its columns.
 */
export const ImageSizesContext = createContext('100vw');

/** A field's value converted to what a prop of this kind holds — or nothing, if it cannot be. */
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;
const ISO_DATETIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?$/;

/**
 * A stored date as people read it, in the site's language: `2026-07-01` → `1 Jul 2026`. A date
 * without a time is formatted in UTC so it cannot move to the day before; a date-time shows the
 * visitor's local time. Anything else — or a locale the browser rejects — is returned as given.
 */
export function formatDate(value: string, locale?: string): string {
  const dateOnly = ISO_DATE.test(value);
  if (!dateOnly && !ISO_DATETIME.test(value)) return value;
  const date = new Date(dateOnly ? `${value}T00:00:00Z` : value);
  if (Number.isNaN(date.getTime())) return value;
  try {
    return new Intl.DateTimeFormat(locale, dateOnly ? { dateStyle: 'medium', timeZone: 'UTC' } : { dateStyle: 'medium', timeStyle: 'short' }).format(date);
  } catch {
    return value;
  }
}

export function coerce(prop: PropDefinition, value: unknown, locale?: string): unknown {
  if (value === undefined || value === null) return undefined;
  // Text shows a date the way people read it; a date or url prop keeps the stored value.
  if (prop.kind === 'text' && typeof value === 'string') return formatDate(value, locale);
  switch (prop.kind) {
    case 'media':
      if (prop.multiple) return mediaList(value);
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
  locale?: string,
): Record<string, unknown> {
  if (!bind || !scope) return props;
  const out = { ...props };
  for (const def of definitions) {
    const path = bind[def.name];
    if (path === undefined) continue;
    const value = coerce(def, readField(scope, path), locale);
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
