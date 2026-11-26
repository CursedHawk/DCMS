/**
 * Shared, tenant-agnostic types for the DCMS content API runtime. The generated
 * per-tenant layer (types.ts + resolvers.ts, emitted server-side from the tenant's
 * OpenAPI doc) imports these and narrows the generic `data` payloads.
 */

export interface TenantClientOptions {
  /**
   * Base URL of the tenant's content API. Empty string (the default) means
   * same-origin — correct for a published site served on the tenant's domain,
   * where site-host proxies `/api/*` to content-api.
   */
  baseUrl?: string;
  /** Custom fetch (tests / SSR). Defaults to the global `fetch`. */
  fetch?: typeof globalThis.fetch;
  /** Supplies the current visitor JWT for visitor-auth'd endpoints (e.g. `/me`). */
  visitorToken?: () => string | null | undefined;
  /** Custom WebSocket constructor for realtime channels (tests). Defaults to the global `WebSocket`. */
  webSocket?: typeof WebSocket;
}

/** A realtime connection's state; `closed` after `close()`, or where there is no WebSocket (SSR). */
export type RealtimeState = 'connecting' | 'open' | 'reconnecting' | 'closed';

/** One message on a channel, as every subscriber receives it. */
export interface RealtimeMessage<T = unknown> {
  channel: string;
  /** The key, on a keyed channel. */
  key: string | null;
  payload: T;
  /** The signed-in site user or visitor who published it from a browser; null for the server or an anonymous browser. */
  sender: string | null;
}

/** What one channel of a WebSockets instance carries, as the generated client describes it. */
export interface ChannelSpec {
  payload: unknown;
  /** Subscribed to by key (`channel/key`): a room, an order, a user. */
  keyed: boolean;
  /** Whether browsers may publish on it at all (the server still checks who). */
  publish: boolean;
}

type ChannelKey<S extends ChannelSpec> = S['keyed'] extends true ? [key: string] : [];

/**
 * A WebSockets instance's channels, typed (ADR 0037). Subscriptions survive reconnects; each
 * `subscribe` returns its own unsubscribe. The generated client returns one of these from
 * `api.<instance>.realtime.connect()`.
 */
export interface RealtimeChannels<C extends Record<string, ChannelSpec> = Record<string, ChannelSpec>> {
  subscribe<K extends keyof C & string>(
    channel: K,
    handler: (payload: C[K]['payload'], message: RealtimeMessage<C[K]['payload']>) => void,
    ...key: ChannelKey<C[K]>
  ): () => void;
  publish<K extends { [N in keyof C & string]: C[N]['publish'] extends true ? N : never }[keyof C & string]>(
    channel: K,
    payload: C[K]['payload'],
    ...key: ChannelKey<C[K]>
  ): Promise<void>;
  readonly state: RealtimeState;
  onState(handler: (state: RealtimeState) => void): () => void;
  /** Problems the server reported for a subscription (no access, unknown channel). */
  onError(handler: (error: Error) => void): () => void;
  close(): void;
}

/** A published content item — mirrors ContentItemDto from the delivery API. */
export interface ContentItem<T = Record<string, unknown>> {
  id: string;
  pluginInstanceId: string;
  contentType: string;
  slug: string;
  versionNo: number;
  data: T;
  publishedAt: string;
}

/** The list envelope every content list endpoint returns. */
export interface PagedResult<T = Record<string, unknown>> {
  items: ContentItem<T>[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface ListParams {
  page?: number;
  pageSize?: number;
  /** Only items carrying this tag (case-insensitive). See `listTags()` for what exists. */
  tag?: string;
  /** Restrict the tag match to one field, for content types with more than one tag list. */
  tagField?: string;
}

/** Query and body for `HttpCore.call`, the primitive every generated operation is built on. */
export interface CallOptions {
  /** Appended as a query string; `undefined` and `null` values are dropped. */
  query?: Record<string, string | number | boolean | null | undefined>;
  /** Sent as JSON. */
  body?: unknown;
}

/**
 * A media asset reference — the asset's GUID, resolved to bytes at
 * `/api/media/{assetId}/{variant}`. Branded so the generated types can flag which
 * fields are media without changing the runtime representation (still a string).
 */
export type MediaRef = string & { readonly __dcmsMedia?: unique symbol };

/** A cross-content reference — the referenced item's GUID. */
export type ContentRef = string & { readonly __dcmsContentRef?: unique symbol };

/** Well-known media variants; any server-defined variant string is also accepted. */
export type MediaVariant = 'original' | 'thumb' | (string & {});

export interface SearchHit {
  title: string;
  url: string;
  contentType: string;
}

export interface SearchResult {
  items: SearchHit[];
  total: number;
}

export interface SearchParams {
  q: string;
  limit?: number;
}

export interface AnalyticsEvent {
  type?: string;
  path?: string;
  referrer?: string;
  sessionId?: string;
  props?: Record<string, unknown>;
}

export interface VisitorTokens {
  accessToken: string;
  refreshToken: string;
}

/** An enterprise user signed in to the site (User Authentication), as the edge knows them. */
/**
 * The site's access rules on one path: open it, sign in first, not for this visitor, or the site
 * cannot sign anyone in right now.
 */
export type SiteAccess = 'allow' | 'signin' | 'forbidden' | 'unavailable';

export interface SiteUser {
  name: string | null;
  email: string | null;
  /** Their groups' ids in the tenant's directory. */
  groups: string[];
}

export interface VisitorProfile {
  id: string;
  email: string;
  displayName?: string | null;
}

/** One profile attribute the site defines (VisitorAuth instance config). */
export interface VisitorAttributeDefinition {
  key: string;
  label: string;
  type: 'text' | 'number' | 'boolean' | 'date' | 'select';
  /** Who besides the visitor and the site's admins may read the value. */
  visibility: 'private' | 'plugins' | 'public';
  visitorEditable: boolean;
  options?: string[] | null;
}

/** The signed-in visitor's own profile: every attribute, private ones included. */
export interface VisitorOwnProfile extends VisitorProfile {
  attributes: Record<string, unknown>;
  definitions: VisitorAttributeDefinition[];
}

/** A profile change. An attribute set to `null` is cleared; only visitor-editable ones are accepted. */
export interface VisitorProfileUpdate {
  displayName?: string;
  attributes?: Record<string, unknown>;
}

export interface VisitorRegistration {
  email: string;
  password: string;
  displayName?: string;
}

export interface VisitorCredentials {
  email: string;
  password: string;
}

export interface ChatMessage {
  id: string;
  sender: string;
  body: string;
  sentAt: string;
}

/** What `POST /api/{slug}/forms/{name}` answers with (HTTP 202). */
export interface SubmissionResult {
  submissionId: string;
  message?: string;
}

/** One entry of `GET /api/tags`. */
export interface TagIndexEntry {
  tag: string;
  count: number;
  occurrences: { instance: string; contentType: string; field: string; count: number; url: string }[];
}

export interface TagIndex {
  items: TagIndexEntry[];
  totalCount: number;
}

/**
 * A content collection this tenant publishes — one content type of one plugin instance.
 *
 * <p>Emitted by the generator as `collections` so a page can be written against whatever the
 * tenant has, rather than against slugs somebody has to look up. The `*Field` hints name which
 * field of `data` plays which role; each is absent when the content type has no such field.</p>
 */
export interface CollectionInfo {
  /** Plugin instance slug — the first path segment of its delivery URL. */
  instance: string;
  contentType: string;
  /** The instance's name, as the tenant admin wrote it. */
  label: string;
  /** What the tenant admin says this instance is for. */
  description: string;
  titleField?: string;
  summaryField?: string;
  /** A media field holding an image asset id; resolve with `api.media.url(id, variant)`. */
  imageField?: string;
  /** Long-form text; `bodyFormat` says how to render it. */
  bodyField?: string;
  bodyFormat?: 'richtext' | 'markdown' | 'text';
  dateField?: string;
}

export type FormFieldType = 'text' | 'email' | 'date' | 'number' | 'textarea' | 'checkbox';

export interface FormFieldInfo {
  name: string;
  label: string;
  type: FormFieldType;
  required: boolean;
  maxLength?: number;
}

/** A visitor form configured in a Forms plugin instance, emitted by the generator as `forms`. */
export interface FormInfo {
  instance: string;
  name: string;
  title: string;
  fields: FormFieldInfo[];
}
