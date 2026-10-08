import { afterEach, describe, expect, it, vi } from 'vitest';
import { guardSiteRoute, guardedRoutes } from './access';

const assign = vi.fn();
const answers = new Map<string, () => Promise<Response>>();
const fetchMock = vi.fn((url: string) => {
  const path = new URL(url, 'https://site.test').searchParams.get('path')!;
  return (answers.get(path) ?? (() => Promise.resolve(Response.json({ [path]: 'allow' }))))();
});
vi.stubGlobal('window', { location: { assign } });
vi.stubGlobal('fetch', fetchMock);

const decided = (p: Promise<boolean>) => Promise.race([p, new Promise((r) => setTimeout(() => r('pending'), 20))]);

afterEach(() => {
  assign.mockReset();
  fetchMock.mockClear();
});

describe('guardSiteRoute', () => {
  it('draws an allowed route, asking the edge once per path', async () => {
    expect(await guardSiteRoute('/open')).toBe(true);
    expect(await guardSiteRoute('/open')).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(assign).not.toHaveBeenCalled();
  });

  it('sends someone who must sign in to the edge and back, and never draws the route', async () => {
    answers.set('/portal', () => Promise.resolve(Response.json({ '/portal': 'signin' })));
    expect(await decided(guardSiteRoute('/portal'))).toBe('pending');
    expect(assign).toHaveBeenCalledWith('/.edge/site/signin?returnUrl=%2Fportal');
  });

  it("loads a route that is not theirs for real, so the edge answers with its own page", async () => {
    answers.set('/admin', () => Promise.resolve(Response.json({ '/admin': 'forbidden' })));
    expect(await decided(guardSiteRoute('/admin'))).toBe('pending');
    expect(assign).toHaveBeenCalledWith('/admin');
  });

  it('lets the route draw off the edge (404) or when the edge cannot be asked, and asks again later', async () => {
    answers.set('/preview', () => Promise.resolve(new Response(null, { status: 404 })));
    expect(await guardSiteRoute('/preview')).toBe(true);
    answers.set('/flaky', () => Promise.reject(new Error('offline')));
    expect(await guardSiteRoute('/flaky')).toBe(true);
    await guardSiteRoute('/flaky');
    expect(fetchMock.mock.calls.filter(([u]) => u.includes('flaky'))).toHaveLength(2);
  });
});

describe('guardedRoutes', () => {
  it('guards the shell on every path change and lets the first page draw without asking', () => {
    const { routes, hydrationData } = guardedRoutes([{ path: '/', children: [] }, { path: '*' }]);
    const shell = routes[0]!;
    expect(shell.loader).toBeTypeOf('function');
    expect(hydrationData.loaderData[shell.id!]).toBeNull();
    const revalidate = shell.shouldRevalidate!;
    const args = (from: string, to: string) =>
      ({ currentUrl: new URL(from, 'https://s.test'), nextUrl: new URL(to, 'https://s.test'), defaultShouldRevalidate: false }) as Parameters<typeof revalidate>[0];
    expect(revalidate(args('/a', '/b'))).toBe(true);
    expect(revalidate(args('/a', '/a?page=2'))).toBe(false);
    expect(routes[1]).toEqual({ path: '*' });
  });
});
