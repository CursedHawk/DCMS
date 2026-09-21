import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CSRF_HEADER, createBffAuth, csrfHeader } from './auth';

/**
 * The BFF authentication client (ADR 0014): the console's half of moving tokens out of the
 * browser.
 *
 * <p>The property worth testing hardest is an absence — `getAccessToken()` returns undefined,
 * because that is what makes the console send no Authorization header, which is in turn the
 * one signal that tells the edge to attach its own. If it ever returned a string again, the
 * edge would pass the request through untouched and the cutover would silently un-happen.</p>
 */

const fetchMock = vi.fn();
const assign = vi.fn();

/** A Response stand-in; this client only reads `ok` and `json()`. */
function res(status: number, body?: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as unknown as Response;
}

beforeEach(() => {
  fetchMock.mockReset();
  assign.mockReset();
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('window', { location: { pathname: '/sites/42', search: '?tab=files', assign } });
  vi.stubGlobal('document', { cookie: '' });
});
afterEach(() => vi.unstubAllGlobals());

describe('createBffAuth', () => {
  it('has no access token to give out', async () => {
    // The whole mode rests on this. See the note above.
    await expect(createBffAuth().getAccessToken()).resolves.toBeUndefined();
  });

  it('reads the session the edge reports', async () => {
    fetchMock.mockResolvedValue(res(200, { sub: 'user-1', name: 'Ops', email: 'ops@example.test' }));

    await expect(createBffAuth().getUser()).resolves.toEqual({
      profile: { sub: 'user-1', name: 'Ops', email: 'ops@example.test' },
    });
    expect(fetchMock).toHaveBeenCalledWith('/.edge/me', expect.anything());
  });

  it('treats a 401 as signed out rather than as an error', async () => {
    // The edge answers 401 instead of redirecting precisely so a fetch caller can read it; a
    // redirect to identity's login page would come back as HTML this code cannot use.
    fetchMock.mockResolvedValue(res(401));

    await expect(createBffAuth().getUser()).resolves.toBeNull();
  });

  it('treats a session with no subject as no session', async () => {
    // Defensive: `sub` is the one field the shell cannot do without, and half a session
    // renders a signed-in chrome whose hub connections identify nobody.
    fetchMock.mockResolvedValue(res(200, { email: 'ops@example.test' }));

    await expect(createBffAuth().getUser()).resolves.toBeNull();
  });

  it('keeps the session when the network fails rather than signing the operator out', async () => {
    const auth = createBffAuth();
    fetchMock.mockResolvedValue(res(200, { sub: 'user-1' }));
    await auth.getUser();

    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));

    // Signing someone out of a page they are working in because one request did not leave the
    // machine is worse than leaving a shell whose next request will fail visibly.
    await expect(auth.getUser()).resolves.toEqual({ profile: { sub: 'user-1', name: undefined, email: undefined } });
  });

  it('notifies subscribers when the session changes, and not when it does not', async () => {
    const auth = createBffAuth();
    const seen: (string | null)[] = [];
    auth.subscribe((user) => seen.push(user?.profile.sub ?? null));

    fetchMock.mockResolvedValue(res(200, { sub: 'user-1' }));
    await auth.getUser();
    await auth.getUser();

    fetchMock.mockResolvedValue(res(401));
    await auth.getUser();

    // Signing in, then signing out. The second identical read must not churn the shell.
    expect(seen).toEqual(['user-1', null]);
  });

  it('stops notifying after unsubscribe', async () => {
    const auth = createBffAuth();
    const seen: unknown[] = [];
    const unsubscribe = auth.subscribe((user) => seen.push(user));
    unsubscribe();

    fetchMock.mockResolvedValue(res(200, { sub: 'user-1' }));
    await auth.getUser();

    expect(seen).toEqual([]);
  });

  it('sends sign-in and sign-up to the edge, carrying where to come back to', async () => {
    const auth = createBffAuth();

    await auth.login();
    expect(assign).toHaveBeenCalledWith('/.edge/signin?returnUrl=%2Fsites%2F42%3Ftab%3Dfiles');

    await auth.register();
    // Same redirect with the hint the edge forwards to identity, which is what lands a new
    // user on the sign-up form instead of the sign-in form.
    expect(assign).toHaveBeenCalledWith('/.edge/signin?flow=register&returnUrl=%2Fsites%2F42%3Ftab%3Dfiles');
  });

  it('sends sign-out to the edge, which is the only thing that can end the session', async () => {
    await createBffAuth().logout();

    expect(assign).toHaveBeenCalledWith('/.edge/signout');
  });

  it('has nothing local to clear', async () => {
    // Not a no-op by omission: the cookie is HttpOnly, so there is genuinely nothing here to
    // drop, and the delete-account path relies on that being true rather than silently failing.
    await expect(createBffAuth().clearLocalSession()).resolves.toBeUndefined();
  });
});

describe('csrfHeader', () => {
  it('sends the token the edge minted', () => {
    vi.stubGlobal('document', { cookie: 'other=1; dcms.csrf=abc%2Fdef; another=2' });

    expect(csrfHeader()).toEqual({ [CSRF_HEADER]: 'abc/def' });
  });

  it('sends nothing at all when there is no token', () => {
    // Not an empty header: the edge refuses a write whose token does not verify, so an absent
    // header and a wrong one are the same refusal — and bearer mode has no such cookie.
    vi.stubGlobal('document', { cookie: 'other=1' });

    expect(csrfHeader()).toEqual({});
  });

  it('is not fooled by a cookie whose name merely ends the same way', () => {
    vi.stubGlobal('document', { cookie: 'not-dcms.csrf=wrong' });

    expect(csrfHeader()).toEqual({});
  });

  it('is not fooled by a cookie that matches only because a dot is a wildcard', () => {
    // The name is interpolated into a regex, and `.` would otherwise match anything.
    vi.stubGlobal('document', { cookie: 'dcmsXcsrf=wrong' });

    expect(csrfHeader()).toEqual({});
  });
});
