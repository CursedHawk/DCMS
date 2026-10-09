// @vitest-environment jsdom
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/*
 * hydrate.js — the Mode A runtime every published page ships — deciding whether to ask for
 * consent and when Google Analytics may load. The same rules as the React runtime's
 * src/dcms/analytics.ts (packages/site-template-react/test/analytics.test.ts).
 */

const here = dirname(fileURLToPath(import.meta.url));
const HYDRATE = readFileSync(resolve(here, '../../../src/Services/Dcms.SiteBuilder/Runtime/hydrate.js'), 'utf8');

let urls: string[];

function run(policy: Record<string, unknown>, ga: { measurementId?: string }) {
  const block = document.createElement('script');
  block.type = 'application/json';
  block.id = 'dcms-consent';
  block.textContent = JSON.stringify(policy);
  document.body.appendChild(block);
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      urls.push(url);
      return new Response(JSON.stringify(url === '/api/ga/config' ? ga : {}), { status: 200 });
    }),
  );
  new Function(HYDRATE)();
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));
const banner = () => document.querySelector<HTMLElement>('.dcms-consent');
const gtagScripts = () =>
  [...document.head.querySelectorAll<HTMLScriptElement>('script')].filter((s) => s.src.includes('googletagmanager'));

beforeEach(() => {
  urls = [];
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  document.head.innerHTML = '';
  document.body.innerHTML = '';
  const w = window as unknown as { gtag?: unknown; dataLayer?: unknown };
  delete w.gtag;
  delete w.dataLayer;
});

describe('hydrate.js consent and Google Analytics', () => {
  it('asks on a GA-only site, names Google, and loads the tag once after acceptance', async () => {
    run({ mode: 'banner', analytics: false }, { measurementId: 'G-TEST1234' });
    await settle();

    expect(banner()?.textContent).toContain('Google Analytics');
    expect(gtagScripts()).toHaveLength(0);

    banner()!.querySelector<HTMLButtonElement>('.dcms-consent-accept')!.click();
    expect(gtagScripts().map((s) => s.src)).toEqual(['https://www.googletagmanager.com/gtag/js?id=G-TEST1234']);
    expect(urls, 'the DCMS collector is off, so no beacon').not.toContain('/api/collect');
  });

  it('loads nothing after a decline', async () => {
    run({ mode: 'banner', analytics: false }, { measurementId: 'G-TEST1234' });
    await settle();
    banner()!.querySelector<HTMLButtonElement>('.dcms-consent-decline')!.click();
    expect(gtagScripts()).toHaveLength(0);
  });

  it('shows no banner when neither DCMS analytics nor GA is on', async () => {
    run({ mode: 'banner', analytics: false }, {});
    await settle();
    expect(banner()).toBeNull();
  });

  it('keeps the anonymous wording when only DCMS analytics asks', async () => {
    run({ mode: 'banner', analytics: true }, {});
    await settle();
    expect(banner()?.textContent).toContain('anonymous analytics');
  });

  it('loads the tag at start for a visitor who already accepted, alongside the DCMS page view', async () => {
    localStorage.setItem('dcms-consent', 'granted');
    run({ mode: 'banner', analytics: true }, { measurementId: 'G-TEST1234' });
    await settle();
    expect(gtagScripts()).toHaveLength(1);
    expect(urls).toContain('/api/collect');
  });
});
