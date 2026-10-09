// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/*
 * The generated SPA collector (shared/src/dcms/analytics.ts). Kept outside shared/, which is
 * embedded into every tenant's site as-is.
 */

type Beacon = { type: string; path: string; referrer: string | null };

let beacons: Beacon[];

// Each test installs a fresh copy of the module, which patches history and adds listeners;
// both are undone after the test so one copy's tracker cannot report into the next test.
const { pushState, replaceState } = history;
const added: [string, EventListenerOrEventListenerObject][] = [];
const realAdd = window.addEventListener.bind(window);

async function install() {
  vi.resetModules();
  const mod = await import('../shared/src/dcms/analytics');
  mod.installAnalytics();
  await settle();
  return mod;
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

beforeEach(() => {
  beacons = [];
  window.addEventListener = ((type: string, listener: EventListenerOrEventListenerObject, opts?: unknown) => {
    added.push([type, listener]);
    realAdd(type, listener, opts as AddEventListenerOptions);
  }) as typeof window.addEventListener;
  history.replaceState(null, '', '/');
  localStorage.setItem('dcms-consent', 'granted');
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/api/collect')) beacons.push(JSON.parse(String(init?.body)));
      return new Response(JSON.stringify({ enabled: true }), { status: 200 });
    }),
  );
});

afterEach(() => {
  for (const [type, listener] of added.splice(0)) window.removeEventListener(type, listener);
  window.addEventListener = realAdd;
  Object.assign(history, { pushState, replaceState });
  vi.unstubAllGlobals();
  localStorage.clear();
});

const pageviews = () => beacons.filter((b) => b.type === 'pageview').map((b) => b.path);

describe('route-change page views', () => {
  it('counts the landing page once, then each push and back/forward', async () => {
    await install();
    history.pushState(null, '', '/pricing');
    await settle();
    history.pushState(null, '', '/pricing?utm_source=news');
    await settle();
    history.back();
    await new Promise((resolve) => addEventListener('popstate', resolve, { once: true }));
    await settle();

    expect(pageviews()).toEqual(['/', '/pricing', '/pricing?utm_source=news', '/pricing']);
  });

  it('ignores a replaceState that only rewrites the query, but counts one that changes the page', async () => {
    await install();
    history.replaceState(null, '', '/?q=shoes');
    await settle();
    history.replaceState(null, '', '/?q=shoes&sort=price');
    await settle();
    history.replaceState(null, '', '/home');
    await settle();

    expect(pageviews()).toEqual(['/', '/home']);
  });

  it('sends the referrer with the first page view only', async () => {
    Object.defineProperty(document, 'referrer', { value: 'https://search.example/', configurable: true });
    await install();
    history.pushState(null, '', '/next');
    await settle();

    expect(beacons.map((b) => b.referrer)).toEqual(['https://search.example/', null]);
  });

  it('sends nothing from an automated browser', async () => {
    Object.defineProperty(navigator, 'webdriver', { value: true, configurable: true });
    try {
      await install();
      history.pushState(null, '', '/next');
      await settle();
      expect(beacons).toEqual([]);
    } finally {
      Object.defineProperty(navigator, 'webdriver', { value: false, configurable: true });
    }
  });
});
