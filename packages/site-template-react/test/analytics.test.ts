// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/*
 * The generated SPA collector (shared/src/dcms/analytics.ts). Kept outside shared/, which is
 * embedded into every tenant's site as-is.
 */

type Beacon = { type: string; path: string; referrer: string | null };

let beacons: Beacon[];
/** What the mocked server says: the DCMS collector's status and the site's GA config. */
let server: { enabled: boolean; ga: { measurementId?: string } };

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
  server = { enabled: true, ga: {} };
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
      const body = url.endsWith('/api/ga/config') ? server.ga : { enabled: server.enabled };
      return new Response(JSON.stringify(body), { status: 200 });
    }),
  );
});

afterEach(() => {
  for (const [type, listener] of added.splice(0)) window.removeEventListener(type, listener);
  window.addEventListener = realAdd;
  Object.assign(history, { pushState, replaceState });
  vi.unstubAllGlobals();
  localStorage.clear();
  document.head.querySelectorAll('script').forEach((s) => s.remove());
  const w = window as unknown as { gtag?: unknown; dataLayer?: unknown };
  delete w.gtag;
  delete w.dataLayer;
});

const gtagScripts = () =>
  [...document.head.querySelectorAll<HTMLScriptElement>('script')].map((s) => s.src).filter((src) => src.includes('googletagmanager'));

describe('Google Analytics', () => {
  it('asks for consent on a GA-only site and loads the tag once, only after acceptance', async () => {
    localStorage.removeItem('dcms-consent');
    server = { enabled: false, ga: { measurementId: 'G-TEST1234' } };
    const mod = await install();

    expect(mod.consentNeeded()).toBe(true);
    expect(mod.gaMeasurementId()).toBe('G-TEST1234');
    expect(gtagScripts()).toEqual([]);

    mod.setConsent('granted');
    mod.setConsent('granted');
    expect(gtagScripts()).toEqual(['https://www.googletagmanager.com/gtag/js?id=G-TEST1234']);
    expect(beacons, 'the DCMS collector is off, so it still sends nothing').toEqual([]);
  });

  it('loads nothing when the visitor declines', async () => {
    localStorage.removeItem('dcms-consent');
    server.ga = { measurementId: 'G-TEST1234' };
    const mod = await install();
    mod.setConsent('denied');
    expect(gtagScripts()).toEqual([]);
  });

  it('loads at start for a visitor who already accepted', async () => {
    server.ga = { measurementId: 'G-TEST1234' };
    await install();
    expect(gtagScripts()).toHaveLength(1);
  });

  it('shows no banner when neither DCMS analytics nor GA is on', async () => {
    localStorage.removeItem('dcms-consent');
    server = { enabled: false, ga: {} };
    const mod = await install();
    expect(mod.consentNeeded()).toBe(false);
  });

  it('refuses a malformed id rather than putting it in a script URL', async () => {
    server.ga = { measurementId: 'G-1"><script>' };
    const mod = await install();
    expect(mod.gaMeasurementId()).toBeNull();
    expect(gtagScripts()).toEqual([]);
  });
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
