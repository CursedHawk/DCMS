/**
 * `@dcms/api-client` — the tenant-agnostic runtime for a DCMS content API.
 *
 * This package is hand-written and stable. The typed, per-tenant surface
 * (`api.blog.posts.list()` etc.) is produced by the server-side emitter from the
 * tenant's OpenAPI document and layered on top of the primitives exported here —
 * see the generated `resolvers.ts` / `index.ts` in a downloaded client or starter.
 *
 * Used directly, `createTenantClient(options)` returns those primitives, so it is
 * usable without any generation:
 *
 *   const api = createTenantClient({ baseUrl: '' });
 *   const posts = await api.listContent('blog', 'post', { page: 1 });
 */
import type {
  AnalyticsEvent,
  ChatMessage,
  ContentItem,
  ListParams,
  MediaVariant,
  PagedResult,
  SearchParams,
  SearchResult,
  TenantClientOptions,
  VisitorCredentials,
  VisitorProfile,
  VisitorRegistration,
  VisitorTokens,
} from './types';

export * from './types';

/** Thrown for any non-2xx response from the content API. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly url: string,
    message: string,
    readonly body?: unknown,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** The low-level primitives every generated resolver tree is built from. */
export interface HttpCore {
  listContent<T = Record<string, unknown>>(
    slug: string,
    contentType: string,
    params?: ListParams,
  ): Promise<PagedResult<T>>;
  getContent<T = Record<string, unknown>>(
    slug: string,
    contentType: string,
    itemSlug: string,
  ): Promise<ContentItem<T>>;
  /** Builds a media URL; pure (no request). */
  mediaUrl(assetId: string, variant?: MediaVariant): string;
  search(slug: string, params: SearchParams): Promise<SearchResult>;
  /** Fire-and-forget analytics beacon. `slug` omitted → the tenant's default analytics instance. */
  collect(event: AnalyticsEvent, slug?: string): Promise<void>;
  visitorAuth(slug: string): VisitorAuthApi;
  chatHistory(slug: string, conversationId: string): Promise<ChatMessage[]>;
  /** A typed list/get pair for one content type — usable without codegen. */
  content<T = Record<string, unknown>>(slug: string, contentType: string): ContentResolver<T>;
  /** Escape hatch for endpoints not covered by a helper. */
  request<T>(path: string, init?: RequestInit): Promise<T>;
}

export interface VisitorAuthApi {
  register(body: VisitorRegistration): Promise<VisitorTokens>;
  login(body: VisitorCredentials): Promise<VisitorTokens>;
  refresh(refreshToken: string): Promise<VisitorTokens>;
  me(): Promise<VisitorProfile>;
}

export interface ContentResolver<T = Record<string, unknown>> {
  list(params?: ListParams): Promise<PagedResult<T>>;
  get(itemSlug: string): Promise<ContentItem<T>>;
}

export function createHttpCore(options: TenantClientOptions = {}): HttpCore {
  const fetchImpl = options.fetch ?? globalThis.fetch;
  if (typeof fetchImpl !== 'function') {
    throw new Error('No fetch implementation available; pass options.fetch.');
  }
  // Trim a trailing slash so `${base}/api/...` never double-slashes. Empty base
  // stays empty → same-origin relative requests.
  const base = (options.baseUrl ?? '').replace(/\/+$/, '');

  function url(path: string): string {
    return `${base}${path}`;
  }

  async function request<T>(path: string, init?: RequestInit): Promise<T> {
    const full = url(path);
    const headers = new Headers(init?.headers);
    const token = options.visitorToken?.();
    if (token) {
      headers.set('Authorization', `Bearer ${token}`);
    }
    const response = await fetchImpl(full, { ...init, headers });
    if (!response.ok) {
      const body = await safeBody(response);
      throw new ApiError(response.status, full, `${init?.method ?? 'GET'} ${path} failed: ${response.status}`, body);
    }
    if (response.status === 204 || response.status === 202) {
      return undefined as T;
    }
    return (await response.json()) as T;
  }

  async function jsonPost<T>(path: string, body: unknown): Promise<T> {
    return request<T>(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
  }

  function doList<T>(slug: string, contentType: string, params?: ListParams): Promise<PagedResult<T>> {
    const query = new URLSearchParams();
    if (params?.page != null) query.set('page', String(params.page));
    if (params?.pageSize != null) query.set('pageSize', String(params.pageSize));
    const qs = query.toString();
    return request<PagedResult<T>>(`/api/${slug}/${contentType}${qs ? `?${qs}` : ''}`);
  }

  function doGet<T>(slug: string, contentType: string, itemSlug: string): Promise<ContentItem<T>> {
    return request<ContentItem<T>>(`/api/${slug}/${contentType}/${encodeURIComponent(itemSlug)}`);
  }

  return {
    listContent: doList,
    getContent: doGet,
    content<T = Record<string, unknown>>(slug: string, contentType: string): ContentResolver<T> {
      return {
        list: (params?: ListParams) => doList<T>(slug, contentType, params),
        get: (itemSlug: string) => doGet<T>(slug, contentType, itemSlug),
      };
    },
    mediaUrl(assetId: string, variant: MediaVariant = 'original'): string {
      return url(`/api/media/${assetId}/${variant}`);
    },
    search(slug: string, params: SearchParams): Promise<SearchResult> {
      const query = new URLSearchParams({ q: params.q });
      if (params.limit != null) query.set('limit', String(params.limit));
      return request<SearchResult>(`/api/${slug}/search?${query.toString()}`);
    },
    async collect(event: AnalyticsEvent, slug?: string): Promise<void> {
      const path = slug ? `/api/${slug}/collect` : '/api/collect';
      await jsonPost<void>(path, event);
    },
    visitorAuth(slug: string): VisitorAuthApi {
      return {
        register: (body: VisitorRegistration) => jsonPost<VisitorTokens>(`/api/${slug}/register`, body),
        login: (body: VisitorCredentials) => jsonPost<VisitorTokens>(`/api/${slug}/login`, body),
        refresh: (refreshToken: string) => jsonPost<VisitorTokens>(`/api/${slug}/refresh`, { refreshToken }),
        me: () => request<VisitorProfile>(`/api/${slug}/me`),
      };
    },
    chatHistory(slug: string, conversationId: string): Promise<ChatMessage[]> {
      return request<ChatMessage[]>(`/api/${slug}/chat/conversations/${conversationId}/messages`);
    },
    request,
  };
}

/**
 * Generic client: the raw primitives. The generated per-tenant package re-exports
 * a `createTenantClient` of the same name whose return type is the typed instance
 * tree, so downloaded code gets `api.blog.posts.list()` instead of these.
 */
export function createTenantClient(options: TenantClientOptions = {}): HttpCore {
  return createHttpCore(options);
}

async function safeBody(response: Response): Promise<unknown> {
  try {
    const text = await response.text();
    try {
      return JSON.parse(text);
    } catch {
      return text;
    }
  } catch {
    return undefined;
  }
}
