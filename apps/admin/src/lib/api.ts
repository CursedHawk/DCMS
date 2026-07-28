import { renewSilently } from '../auth';
import { adminHeaders } from '../tenants';

const base = import.meta.env.VITE_ADMIN_API_BASE ?? '/api';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly detail?: unknown,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function send(path: string, init?: RequestInit): Promise<Response> {
  return fetch(`${base}${path}`, {
    ...init,
    headers: { ...(await adminHeaders()), ...init?.headers },
  });
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let res = await send(path, init);

  // The token can go stale between the header read and the server's clock (or
  // be revoked outright). Renew once and replay before treating it as an error;
  // a failed renewal clears the user, which drops the UI to the sign-in screen.
  if (res.status === 401 && (await renewSilently())) {
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
    throw new ApiError(res.status, message, detail);
  }

  if (res.status === 204) return undefined as T;
  const contentType = res.headers.get('content-type') ?? '';
  if (contentType.includes('application/json')) return (await res.json()) as T;
  return (await res.text()) as unknown as T;
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, {
      method: 'POST',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    }),
  put: <T>(path: string, body?: unknown) =>
    request<T>(path, {
      method: 'PUT',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    }),
  patch: <T>(path: string, body?: unknown) =>
    request<T>(path, {
      method: 'PATCH',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    }),
  del: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
  /** Multipart upload (no JSON content-type; browser sets the boundary). */
  upload: <T>(path: string, form: FormData) => request<T>(path, { method: 'POST', body: form }),
  /** Fetches a binary response (e.g. a generated zip) as a Blob. */
  downloadBlob: async (path: string): Promise<Blob> => {
    const res = await fetch(`${base}${path}`, { headers: await adminHeaders() });
    if (!res.ok) {
      throw new ApiError(res.status, `GET ${path} → ${res.status}`);
    }
    return res.blob();
  },
};

/** API path (relative to base) for a media asset's bytes / a named variant. */
export function mediaContentPath(id: string, variant?: string): string {
  const q = variant ? `?variant=${encodeURIComponent(variant)}` : '';
  return `/admin/media/${id}/content${q}`;
}

/**
 * Fetch a protected resource with the bearer token and return an object URL.
 * Needed for media previews: <img src> can't carry the Authorization header, so
 * we fetch the bytes and hand back a blob: URL (caller revokes on unmount).
 */
export async function fetchObjectUrl(path: string): Promise<string> {
  const res = await fetch(`${base}${path}`, { headers: await adminHeaders() });
  if (!res.ok) throw new ApiError(res.status, `fetch ${path} → ${res.status}`);
  return URL.createObjectURL(await res.blob());
}
