import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, createApiClient } from './http';

/** Minimal Response stand-in; the client only touches these members. */
function res(
  status: number,
  body?: unknown,
  headers: Record<string, string> = {},
  contentType = 'application/json',
): Response {
  const h = new Headers(status === 204 ? headers : { 'content-type': contentType, ...headers });
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: h,
    json: async () => {
      if (body === undefined) throw new Error('no body');
      return body;
    },
    text: async () => (typeof body === 'string' ? body : JSON.stringify(body)),
    blob: async () => new Blob([JSON.stringify(body ?? null)]),
  } as unknown as Response;
}

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('crypto', { randomUUID: () => '11111111-2222-3333-4444-555555555555' });
});
afterEach(() => vi.unstubAllGlobals());

/** Awaits a rejection and narrows it, so a call that wrongly SUCCEEDS fails the test. */
async function rejection(p: Promise<unknown>): Promise<ApiError> {
  try {
    await p;
  } catch (e) {
    if (e instanceof ApiError) return e;
    throw e;
  }
  throw new Error('expected the request to reject, but it resolved');
}

const client = (over: Partial<Parameters<typeof createApiClient>[0]> = {}) =>
  createApiClient({ base: '/api', headers: async () => ({ Authorization: 'Bearer t' }), ...over });

describe('request basics', () => {
  it('prefixes the base and returns parsed JSON', async () => {
    fetchMock.mockResolvedValue(res(200, { ok: true }));
    await expect(client().get('/admin/me')).resolves.toEqual({ ok: true });
    expect(fetchMock.mock.calls[0][0]).toBe('/api/admin/me');
  });

  it('sends the ambient headers and originates a request id', async () => {
    fetchMock.mockResolvedValue(res(200, {}));
    await client().get('/x');
    const sent = fetchMock.mock.calls[0][1].headers;
    expect(sent.Authorization).toBe('Bearer t');
    // Dashless, so it reads as one token in a log line.
    expect(sent['X-Dcms-Request-Id']).toBe('11111111222233334444555555555555');
  });

  it('returns undefined for 204 rather than trying to parse a body', async () => {
    fetchMock.mockResolvedValue(res(204));
    await expect(client().del('/x')).resolves.toBeUndefined();
  });

  it('returns text when the response is not JSON', async () => {
    fetchMock.mockResolvedValue(res(200, 'plain', {}, 'text/plain'));
    await expect(client().get('/x')).resolves.toBe('plain');
  });

  it('serialises a body and sets the content type only when there is one', async () => {
    fetchMock.mockResolvedValue(res(200, {}));
    await client().post('/x', { a: 1 });
    expect(fetchMock.mock.calls[0][1].body).toBe('{"a":1}');
    expect(fetchMock.mock.calls[0][1].headers['Content-Type']).toBe('application/json');

    fetchMock.mockResolvedValue(res(200, {}));
    await client().post('/y');
    expect(fetchMock.mock.calls[1][1].body).toBeUndefined();
    expect(fetchMock.mock.calls[1][1].headers['Content-Type']).toBeUndefined();
  });
});

describe('the 401 replay', () => {
  it('renews once and replays, so a token expiring mid-flight is invisible', async () => {
    const renew = vi.fn().mockResolvedValue(true);
    fetchMock.mockResolvedValueOnce(res(401)).mockResolvedValueOnce(res(200, { ok: true }));
    await expect(client({ renew }).get('/x')).resolves.toEqual({ ok: true });
    expect(renew).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('gives up after ONE replay — a second 401 surfaces and drops the app to sign-in', async () => {
    const renew = vi.fn().mockResolvedValue(true);
    fetchMock.mockResolvedValue(res(401));
    await expect(client({ renew }).get('/x')).rejects.toBeInstanceOf(ApiError);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('does not replay when renewal reports failure', async () => {
    const renew = vi.fn().mockResolvedValue(false);
    fetchMock.mockResolvedValue(res(401));
    await expect(client({ renew }).get('/x')).rejects.toMatchObject({ status: 401 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does not replay any other status', async () => {
    const renew = vi.fn().mockResolvedValue(true);
    fetchMock.mockResolvedValue(res(403, { error: 'nope' }));
    await expect(client({ renew }).get('/x')).rejects.toMatchObject({ status: 403 });
    expect(renew).not.toHaveBeenCalled();
  });
});

describe('errors carry the ids support needs', () => {
  it('prefers the response headers, which even a bodiless response has', async () => {
    fetchMock.mockResolvedValue(
      res(500, { traceId: 'from-body' }, { 'X-Dcms-Trace-Id': 'from-header', 'X-Dcms-Request-Id': 'rq' }),
    );
    const err = await rejection(client().get('/x'));
    expect(err.traceId).toBe('from-header');
    expect(err.requestId).toBe('rq');
  });

  it('falls back to the RFC 9457 problem document, which a proxied response still has', async () => {
    fetchMock.mockResolvedValue(res(500, { traceId: 'from-body', requestId: 'rq-body' }));
    const err = await rejection(client().get('/x'));
    expect(err.traceId).toBe('from-body');
    expect(err.requestId).toBe('rq-body');
  });

  it('uses the server’s message when it gives one', async () => {
    fetchMock.mockResolvedValue(res(400, { error: 'Slug already taken' }));
    await expect(client().post('/x', {})).rejects.toThrow('Slug already taken');

    fetchMock.mockResolvedValue(res(400, { title: 'Validation failed' }));
    await expect(client().post('/x', {})).rejects.toThrow('Validation failed');
  });

  it('falls back to method, path and status when the body is not JSON', async () => {
    fetchMock.mockResolvedValue(res(502, undefined, {}, 'text/html'));
    await expect(client().get('/x')).rejects.toThrow('GET /x → 502');
  });
});

describe('the per-request tenant override', () => {
  it('sends a different tenant than the ambient one, and wins over it', async () => {
    fetchMock.mockResolvedValue(res(200, {}));
    const c = createApiClient({
      base: '',
      headers: async () => ({ 'X-Dcms-Tenant': 'ambient' }),
    });
    await c.get('/x', { tenant: 'other' });
    expect(fetchMock.mock.calls[0][1].headers['X-Dcms-Tenant']).toBe('other');
  });

  it('leaves the ambient tenant alone when no override is given', async () => {
    fetchMock.mockResolvedValue(res(200, {}));
    const c = createApiClient({ base: '', headers: async () => ({ 'X-Dcms-Tenant': 'ambient' }) });
    await c.get('/x');
    expect(fetchMock.mock.calls[0][1].headers['X-Dcms-Tenant']).toBe('ambient');
  });
});

describe('url', () => {
  it('joins the base for the callers that need a real href', () => {
    expect(client().url('/admin/media/1/content')).toBe('/api/admin/media/1/content');
  });
});
