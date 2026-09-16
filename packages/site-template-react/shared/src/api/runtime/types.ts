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

export interface VisitorProfile {
  id: string;
  email: string;
  displayName?: string | null;
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
