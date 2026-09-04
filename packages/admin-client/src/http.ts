export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly detail?: unknown,
    /**
     * The server-side trace this failure belongs to. Carried on the error itself rather than
     * looked up later, because by the time a toast is rendered the Response is gone — and this
     * is the only string a user can give support that resolves to the actual failure in Tempo,
     * Loki and the audit log at once.
     */
    readonly traceId?: string,
    readonly requestId?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/**
 * Where the ids come from, in order of trust: the response headers, which every response
 * carries including the ones with no body at all, then the RFC 9457 problem document, which is
 * what a proxied or cached response is most likely to still have.
 */
function traceOf(res: Response, detail: unknown): { traceId?: string; requestId?: string } {
  const body = detail as { traceId?: string; requestId?: string } | undefined;
  return {
    traceId: res.headers.get('X-Dcms-Trace-Id') ?? body?.traceId ?? undefined,
    requestId: res.headers.get('X-Dcms-Request-Id') ?? body?.requestId ?? undefined,
  };
}

/**
 * Correlates one browser action with every server record it produces. The server
 * generates an id when we don't send one, but originating it here is what links a
 * retried request to its first attempt, and a click to the background work it queues.
 */
function newRequestId(): string {
  return crypto.randomUUID().replace(/-/g, '');
}

/**
 * Per-request overrides.
 *
 * `tenant` sends a different `X-Dcms-Tenant` than the ambient selection, so a platform admin
 * can read one tenant's data without switching the whole app to it — the switch costs a full
 * page reload and loses wherever they were. The server still decides: the header only names a
 * tenant, and permissions are resolved per tenant, so this grants nothing a direct call
 * would not.
 *
 * Anything reached this way must carry the tenant in its react-query key. Two tenants sharing
 * one cache entry would show the first tenant's rows under the second tenant's name, which in
 * an audit log is the worst possible kind of wrong.
 */
export interface RequestOptions {
  tenant?: string;
}

export interface ApiClientOptions {
  /** Prefixed to every path. An empty string means same-origin relative requests. */
  base: string;
  /** Ambient headers (bearer token, tenant) resolved per request. */
  headers: () => Promise<Record<string, string>>;
  /**
   * Called once on a 401 before the request is replayed. Returning a falsy value gives up and
   * lets the 401 surface, which is what drops the shell to the sign-in screen.
   */
  renew?: () => Promise<unknown>;
}

export interface ApiClient {
  get<T>(path: string, opts?: RequestOptions): Promise<T>;
  post<T>(path: string, body?: unknown): Promise<T>;
  put<T>(path: string, body?: unknown): Promise<T>;
  patch<T>(path: string, body?: unknown): Promise<T>;
  del<T>(path: string): Promise<T>;
  upload<T>(path: string, form: FormData): Promise<T>;
  uploadWithProgress<T>(
    path: string,
    form: FormData,
    onProgress: (fraction: number) => void,
    signal?: AbortSignal,
  ): Promise<T>;
  downloadBlob(path: string, opts?: RequestOptions): Promise<Blob>;
  /**
   * Fetch a protected resource with the bearer token and return an object URL.
   * Needed for media previews: <img src> can't carry the Authorization header, so
   * we fetch the bytes and hand back a blob: URL (caller revokes on unmount).
   */
  fetchObjectUrl(path: string): Promise<string>;
  /** Absolute URL for a path, for the rare caller that needs one (an <a href>, a form action). */
  url(path: string): string;
}

/** Header override for a request options bag. Applied after the ambient headers, so it wins. */
function overrides(opts?: RequestOptions): Record<string, string> {
  return opts?.tenant ? { 'X-Dcms-Tenant': opts.tenant } : {};
}

/**
 * One typed fetch wrapper per API a SPA talks to.
 *
 * A factory rather than a module singleton because the platform SPA speaks to three origins
 * (platform-api, identity, admin-api) that differ only in their base path — and because
 * baking `adminApiBase` into the module made the whole thing unusable from a second app.
 */
export function createApiClient(options: ApiClientOptions): ApiClient {
  const base = options.base;

  async function send(path: string, init?: RequestInit): Promise<Response> {
    return fetch(`${base}${path}`, {
      ...init,
      headers: {
        'X-Dcms-Request-Id': newRequestId(),
        ...(await options.headers()),
        ...init?.headers,
      },
    });
  }

  async function request<T>(path: string, init?: RequestInit): Promise<T> {
    let res = await send(path, init);

    // The token can go stale between the header read and the server's clock (or
    // be revoked outright). Renew once and replay before treating it as an error;
    // a failed renewal clears the user, which drops the UI to the sign-in screen.
    if (res.status === 401 && options.renew && (await options.renew())) {
      res = await send(path, init);
    }

    if (!res.ok) {
      let detail: unknown;
      let message = `${init?.method ?? 'GET'} ${path} → ${res.status}`;
      try {
        detail = await res.json();
        const errText = (detail as { error?: string; title?: string })?.error
          ?? (detail as { title?: string })?.title;
        if (errText) message = errText;
      } catch {
        /* non-JSON error body */
      }
      const { traceId, requestId } = traceOf(res, detail);
      throw new ApiError(res.status, message, detail, traceId, requestId);
    }

    if (res.status === 204) return undefined as T;
    const contentType = res.headers.get('content-type') ?? '';
    if (contentType.includes('application/json')) return (await res.json()) as T;
    return (await res.text()) as unknown as T;
  }

  async function uploadWithProgress<T>(
    path: string,
    form: FormData,
    onProgress: (fraction: number) => void,
    signal?: AbortSignal,
  ): Promise<T> {
    const headers = await options.headers();
    return new Promise<T>((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open('POST', `${base}${path}`);
      for (const [key, value] of Object.entries(headers)) xhr.setRequestHeader(key, value);

      // `lengthComputable` is false for streamed bodies; leave the caller on its
      // last known value rather than reporting a bogus 0.
      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable && e.total > 0) onProgress(e.loaded / e.total);
      };

      xhr.onload = () => {
        // The bytes are sent, but the server is still processing; callers show this
        // as "finishing" rather than leaving the bar short of the end.
        onProgress(1);
        const body = xhr.responseText;
        let parsed: unknown;
        try {
          parsed = body ? JSON.parse(body) : undefined;
        } catch {
          parsed = body;
        }
        if (xhr.status >= 200 && xhr.status < 300) {
          resolve(parsed as T);
          return;
        }
        const detail = parsed as { error?: string; title?: string; traceId?: string; requestId?: string } | undefined;
        reject(new ApiError(
          xhr.status,
          detail?.error ?? detail?.title ?? `POST ${path} → ${xhr.status}`,
          parsed,
          xhr.getResponseHeader('X-Dcms-Trace-Id') ?? detail?.traceId ?? undefined,
          xhr.getResponseHeader('X-Dcms-Request-Id') ?? detail?.requestId ?? undefined,
        ));
      };
      xhr.onerror = () => reject(new ApiError(0, `POST ${path} → network error`));
      xhr.onabort = () => reject(new DOMException('Upload aborted', 'AbortError'));

      signal?.addEventListener('abort', () => xhr.abort(), { once: true });
      xhr.send(form);
    });
  }

  return {
    get: <T,>(path: string, opts?: RequestOptions) => request<T>(path, { headers: overrides(opts) }),
    post: <T,>(path: string, body?: unknown) =>
      request<T>(path, {
        method: 'POST',
        headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
      }),
    put: <T,>(path: string, body?: unknown) =>
      request<T>(path, {
        method: 'PUT',
        headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
      }),
    patch: <T,>(path: string, body?: unknown) =>
      request<T>(path, {
        method: 'PATCH',
        headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
      }),
    del: <T,>(path: string) => request<T>(path, { method: 'DELETE' }),
    /** Multipart upload (no JSON content-type; browser sets the boundary). */
    upload: <T,>(path: string, form: FormData) => request<T>(path, { method: 'POST', body: form }),
    /*
     * Multipart upload that reports how much of the body has been sent.
     *
     * `fetch` cannot do this — a request body is not observable — so this is the one
     * place the app drops to XMLHttpRequest. Worth it: media and site bundles are
     * large enough that a spinner with no progress reads as a hang.
     *
     * A 401 is not retried the way `request` retries it. Replaying the upload would
     * mean sending the whole file a second time, and the token is read immediately
     * before the send, so the window in which it can expire is a few milliseconds.
     */
    uploadWithProgress,
    /** Fetches a binary response (e.g. a generated zip) as a Blob. */
    downloadBlob: async (path: string, opts?: RequestOptions): Promise<Blob> => {
      const res = await fetch(`${base}${path}`, {
        headers: { ...(await options.headers()), ...overrides(opts) },
      });
      if (!res.ok) {
        const { traceId, requestId } = traceOf(res, undefined);
        throw new ApiError(res.status, `GET ${path} → ${res.status}`, undefined, traceId, requestId);
      }
      return res.blob();
    },
    fetchObjectUrl: async (path: string): Promise<string> => {
      const res = await fetch(`${base}${path}`, { headers: await options.headers() });
      if (!res.ok) {
        const { traceId, requestId } = traceOf(res, undefined);
        throw new ApiError(res.status, `fetch ${path} → ${res.status}`, undefined, traceId, requestId);
      }
      return URL.createObjectURL(await res.blob());
    },
    url: (path: string) => `${base}${path}`,
  };
}
