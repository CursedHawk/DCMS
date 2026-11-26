/**
 * `@dcms/api-client` — the tenant-agnostic runtime for a DCMS content API.
 *
 * This package is hand-written and stable. The typed, per-tenant surface
 * (`api.blog.posts.list()` etc.) is produced by the server-side emitter from the
 * tenant's OpenAPI document and layered on top of the primitives exported here —
 * see the generated `index.ts` in a downloaded client or starter.
 *
 * Used directly, `createTenantClient(options)` returns those primitives, so it is
 * usable without any generation:
 *
 *   const api = createTenantClient({ baseUrl: '' });
 *   const posts = await api.listContent('blog', 'post', { page: 1 });
 */
import type {
  AnalyticsEvent,
  CallOptions,
  ChannelSpec,
  ChatMessage,
  ContentItem,
  ListParams,
  MediaVariant,
  PagedResult,
  RealtimeChannels,
  RealtimeMessage,
  RealtimeState,
  SearchParams,
  SearchResult,
  SiteAccess,
  SiteUser,
  SubmissionResult,
  TagIndex,
  TenantClientOptions,
  VisitorCredentials,
  VisitorProfile,
  VisitorOwnProfile,
  VisitorProfileUpdate,
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

  /**
   * Whether this is a site's "sign in first" — a 401 from the edge's path rules or an instance's
   * API access (User Authentication), e.g. once a session has ended under a long-open app. Send
   * the visitor to `siteSignInUrl()`, which brings them back to the page they were on.
   */
  get signInRequired(): boolean {
    return this.status === 401 && (this.body as { error?: unknown } | undefined)?.error === 'signin_required';
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
  /** Submit a visitor form of a Forms instance. */
  submitForm<TBody = Record<string, unknown>>(
    slug: string,
    formName: string,
    body: TBody,
  ): Promise<SubmissionResult>;
  /** The tags this tenant's published content carries, and where. */
  listTags(params?: { contentType?: string; field?: string }): Promise<TagIndex>;
  /**
   * Any operation in the tenant's OpenAPI document. The generated client builds every operation
   * that has no dedicated helper out of this, so it is also the way to reach one by hand.
   */
  call<T>(method: string, path: string, options?: CallOptions): Promise<T>;
  /** Escape hatch for endpoints not covered by a helper. */
  request<T>(path: string, init?: RequestInit): Promise<T>;
  /**
   * Invokes a contract operation an instance provides (for example
   * `invokeContract('members', 'visitors.identity@1', 'GetCurrent')`). The generated
   * client names each one (`api.members.identity.getCurrent()`); this reaches any of them
   * by name, and sends the visitor token when one is configured.
   */
  invokeContract<T>(slug: string, contractId: string, operation: string, input?: unknown): Promise<T>;
  /**
   * The signed-in enterprise user of a site with User Authentication, or null. The session is
   * the edge's (a cookie on the site's own domain), so this answers only same-origin — on the
   * published site, not from a dev server elsewhere.
   */
  siteUser(): Promise<SiteUser | null>;
  /** Where to send someone to sign in; they come back to `returnUrl` (a path on this site). Pure. */
  siteSignInUrl(returnUrl?: string): string;
  /** Where to send someone to sign out. Pure. */
  siteSignOutUrl(): string;
  /**
   * What the site's access rules say about each path for this visitor — the edge's own decision,
   * for a single-page app's route guard (its navigation never reaches the rules). UX only: the
   * server still decides every real request. All "allow" when the site has no rules.
   */
  siteAccess(paths: string[]): Promise<Record<string, SiteAccess>>;
  /**
   * Connects to a WebSockets instance's channels (`/hub/ws` on the site's domain). The generated
   * client types it per channel as `api.<instance>.realtime.connect()`.
   */
  realtime<C extends Record<string, ChannelSpec> = Record<string, ChannelSpec>>(slug: string): RealtimeChannels<C>;
}

export interface VisitorAuthApi {
  register(body: VisitorRegistration): Promise<VisitorTokens>;
  login(body: VisitorCredentials): Promise<VisitorTokens>;
  refresh(refreshToken: string): Promise<VisitorTokens>;
  me(): Promise<VisitorProfile>;
  /** The signed-in visitor's full profile and the attribute definitions to render it with. */
  getProfile(): Promise<VisitorOwnProfile>;
  updateProfile(update: VisitorProfileUpdate): Promise<VisitorOwnProfile>;
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
    // One key per write, so a double submit is one submission to content-api (ADR 0024).
    if (init?.method && init.method !== 'GET' && !headers.has('Idempotency-Key')) {
      headers.set('Idempotency-Key', crypto.randomUUID());
    }
    const token = options.visitorToken?.();
    if (token) {
      headers.set('Authorization', `Bearer ${token}`);
    }
    const response = await fetchImpl(full, { ...init, headers });
    if (!response.ok) {
      const body = await safeBody(response);
      throw new ApiError(response.status, full, `${init?.method ?? 'GET'} ${path} failed: ${response.status}`, body);
    }
    if (response.status === 204) {
      return undefined as T;
    }
    // Read as text, not json(): a 202 is sometimes a bare acknowledgement (an analytics beacon)
    // and sometimes carries a result (a form submission), and only the body can say which.
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  async function jsonPost<T>(path: string, body: unknown): Promise<T> {
    return request<T>(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
  }

  function withQuery(path: string, query?: CallOptions['query']): string {
    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(query ?? {})) {
      if (value !== undefined && value !== null) search.set(key, String(value));
    }
    const qs = search.toString();
    return qs ? `${path}?${qs}` : path;
  }

  function call<T>(method: string, path: string, options: CallOptions = {}): Promise<T> {
    const upper = method.toUpperCase();
    const init: RequestInit = { method: upper };
    if (options.body !== undefined) {
      init.headers = { 'Content-Type': 'application/json' };
      init.body = JSON.stringify(options.body);
    }
    return request<T>(withQuery(path, options.query), init);
  }

  function doList<T>(slug: string, contentType: string, params?: ListParams): Promise<PagedResult<T>> {
    return request<PagedResult<T>>(
      withQuery(`/api/${slug}/${contentType}`, {
        page: params?.page,
        pageSize: params?.pageSize,
        tag: params?.tag,
        tagField: params?.tagField,
      }),
    );
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
    invokeContract<T>(slug: string, contractId: string, operation: string, input?: unknown): Promise<T> {
      return call<T>('POST', `/api/${slug}/_contracts/${encodeURIComponent(contractId)}/${encodeURIComponent(operation)}`, {
        body: input ?? {},
      });
    },
    async siteUser(): Promise<SiteUser | null> {
      try {
        const res = await fetchImpl(url('/.edge/site/me'), { credentials: 'same-origin', headers: { Accept: 'application/json' } });
        if (!res.ok) return null;
        const me = (await res.json()) as { signedIn: boolean } & SiteUser;
        return me.signedIn ? { name: me.name, email: me.email, groups: me.groups ?? [] } : null;
      } catch {
        return null;
      }
    },
    siteSignInUrl(returnUrl?: string): string {
      const back = returnUrl ?? (typeof location === 'undefined' ? '/' : location.pathname + location.search);
      return url(`/.edge/site/signin?returnUrl=${encodeURIComponent(back)}`);
    },
    siteSignOutUrl(): string {
      return url('/.edge/site/signout');
    },
    async siteAccess(paths: string[]): Promise<Record<string, SiteAccess>> {
      const query = new URLSearchParams();
      for (const path of paths) query.append('path', path);
      const res = await fetchImpl(url(`/.edge/site/access?${query}`), { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      // Not behind the edge (a dev server elsewhere): there are no rules to ask.
      if (res.status === 404) return Object.fromEntries(paths.map((p) => [p, 'allow' as const]));
      if (!res.ok) throw new ApiError(res.status, res.url, `site access check failed: ${res.status}`, await safeBody(res));
      return (await res.json()) as Record<string, SiteAccess>;
    },
    visitorAuth(slug: string): VisitorAuthApi {
      return {
        register: (body: VisitorRegistration) => jsonPost<VisitorTokens>(`/api/${slug}/register`, body),
        login: (body: VisitorCredentials) => jsonPost<VisitorTokens>(`/api/${slug}/login`, body),
        refresh: (refreshToken: string) => jsonPost<VisitorTokens>(`/api/${slug}/refresh`, { refreshToken }),
        me: () => request<VisitorProfile>(`/api/${slug}/me`),
        getProfile: () => request<VisitorOwnProfile>(`/api/${slug}/me/profile`),
        updateProfile: (update: VisitorProfileUpdate) =>
          call<VisitorOwnProfile>('PUT', `/api/${slug}/me/profile`, { body: update }),
      };
    },
    chatHistory(slug: string, conversationId: string): Promise<ChatMessage[]> {
      return request<ChatMessage[]>(`/api/${slug}/chat/conversations/${conversationId}/messages`);
    },
    submitForm<TBody>(slug: string, formName: string, body: TBody): Promise<SubmissionResult> {
      return jsonPost<SubmissionResult>(`/api/${slug}/forms/${encodeURIComponent(formName)}`, body);
    },
    listTags(params?: { contentType?: string; field?: string }): Promise<TagIndex> {
      return request<TagIndex>(withQuery('/api/tags', params));
    },
    realtime<C extends Record<string, ChannelSpec>>(slug: string): RealtimeChannels<C> {
      const socket = options.webSocket ?? (typeof WebSocket === 'undefined' ? undefined : WebSocket);
      const origin = base || (typeof location === 'undefined' ? '' : location.origin);
      const address = () => {
        const token = options.visitorToken?.();
        const query = `instance=${encodeURIComponent(slug)}${token ? `&access_token=${encodeURIComponent(token)}` : ''}`;
        return `${origin.replace(/^http/, 'ws')}/hub/ws?${query}`;
      };
      return openRealtime(socket && origin ? socket : undefined, address) as unknown as RealtimeChannels<C>;
    },
    call,
    request,
  };
}

/** The record separator ending every SignalR JSON-protocol frame. */
const RS = '\u001e';

/**
 * A SignalR JSON-protocol client over the native WebSocket, enough for `/hub/ws`: the handshake,
 * invocations with completions, the server's `message` calls, pings and close. Reconnects with
 * backoff and subscribes again to everything it held.
 *
 * ponytail: no negotiate, no other transports, no MessagePack — the hub accepts WebSockets only
 * (ADR 0037). Switch to `@microsoft/signalr` if that ever changes.
 */
function openRealtime(socketType: typeof WebSocket | undefined, address: () => string): RealtimeChannels {
  type Handler = (payload: unknown, message: RealtimeMessage) => void;
  const subscriptions = new Map<string, { channel: string; key: string | null; handlers: Set<Handler> }>();
  const pending = new Map<string, { resolve: () => void; reject: (e: Error) => void }>();
  const stateHandlers = new Set<(state: RealtimeState) => void>();
  const errorHandlers = new Set<(error: Error) => void>();
  let state: RealtimeState = socketType ? 'connecting' : 'closed';
  let ws: WebSocket | undefined;
  let ready: Promise<void> = Promise.resolve();
  let nextId = 1;
  let attempt = 0;
  let closed = !socketType;
  let ping: ReturnType<typeof setInterval> | undefined;

  const setState = (next: RealtimeState) => {
    if (state === next) return;
    state = next;
    for (const handler of stateHandlers) handler(next);
  };
  const fail = (error: Error) => {
    if (errorHandlers.size === 0) console.warn('[dcms realtime]', error.message);
    for (const handler of errorHandlers) handler(error);
  };
  const id = (channel: string, key: string | null) => (key == null ? channel : `${channel}/${key}`);

  function invoke(target: string, args: unknown[]): Promise<void> {
    return ready.then(
      () =>
        new Promise<void>((resolve, reject) => {
          if (!ws || ws.readyState !== 1) return reject(new Error('The realtime connection is not open.'));
          const invocationId = String(nextId++);
          pending.set(invocationId, { resolve, reject });
          ws.send(JSON.stringify({ type: 1, invocationId, target, arguments: args }) + RS);
        }),
    );
  }

  function connect() {
    if (closed || !socketType) return;
    let handshaken = false;
    let opened!: () => void;
    ready = new Promise<void>((resolve) => (opened = resolve));
    const socket = new socketType(address());
    ws = socket;
    socket.onopen = () => socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
    socket.onmessage = (event) => {
      for (const frame of String(event.data).split(RS)) {
        if (!frame) continue;
        const message = JSON.parse(frame) as {
          type?: number;
          error?: string;
          target?: string;
          arguments?: unknown[];
          invocationId?: string;
          allowReconnect?: boolean;
        };
        if (!handshaken) {
          // The handshake answer is `{}`, or `{ error }` for a refused protocol.
          if (message.error) return fail(new Error(message.error));
          handshaken = true;
          attempt = 0;
          setState('open');
          ping = setInterval(() => socket.readyState === 1 && socket.send(JSON.stringify({ type: 6 }) + RS), 15_000);
          opened();
          // Everything held before a reconnect is subscribed again.
          for (const sub of subscriptions.values()) invoke('Subscribe', [sub.channel, sub.key]).catch(fail);
          continue;
        }
        if (message.type === 1 && message.target === 'message') {
          const [channel, key, payload, sender] = (message.arguments ?? []) as [string, string | null, unknown, string | null];
          const sub = subscriptions.get(id(channel, key ?? null));
          for (const handler of sub?.handlers ?? []) handler(payload, { channel, key: key ?? null, payload, sender: sender ?? null });
        } else if (message.type === 3 && message.invocationId) {
          const call = pending.get(message.invocationId);
          pending.delete(message.invocationId);
          if (message.error) call?.reject(new Error(message.error));
          else call?.resolve();
        } else if (message.type === 7) {
          if (message.error) fail(new Error(message.error));
          if (message.allowReconnect === false) closed = true;
        }
      }
    };
    socket.onclose = () => {
      clearInterval(ping);
      for (const call of pending.values()) call.reject(new Error('The realtime connection closed.'));
      pending.clear();
      if (closed) return setState('closed');
      setState('reconnecting');
      const delay = [0, 2_000, 5_000, 10_000, 30_000][Math.min(attempt++, 4)] + Math.random() * 1_000;
      setTimeout(connect, delay);
    };
  }

  connect();

  return {
    get state() {
      return state;
    },
    subscribe(channel: string, handler: Handler, key?: string) {
      const name = id(channel, key ?? null);
      let sub = subscriptions.get(name);
      if (!sub) {
        sub = { channel, key: key ?? null, handlers: new Set() };
        subscriptions.set(name, sub);
        invoke('Subscribe', [channel, key ?? null]).catch(fail);
      }
      sub.handlers.add(handler);
      return () => {
        sub.handlers.delete(handler);
        if (sub.handlers.size === 0 && subscriptions.get(name) === sub) {
          subscriptions.delete(name);
          invoke('Unsubscribe', [channel, key ?? null]).catch(() => undefined);
        }
      };
    },
    publish(channel: string, payload: unknown, key?: string) {
      return invoke('Publish', [channel, key ?? null, payload]);
    },
    onState(handler: (state: RealtimeState) => void) {
      stateHandlers.add(handler);
      return () => stateHandlers.delete(handler);
    },
    onError(handler: (error: Error) => void) {
      errorHandlers.add(handler);
      return () => errorHandlers.delete(handler);
    },
    close() {
      closed = true;
      ws?.close();
      setState('closed');
    },
  } as unknown as RealtimeChannels;
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
