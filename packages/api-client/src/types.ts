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
