// @vitest-environment jsdom
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it, vi } from 'vitest';

/*
 * hydrate.js's video layout on a published Mode A page: a YouTube item (the YouTube plugin's
 * `url`) plays in YouTube's privacy-enhanced player, a hosted file still in a <video> tag.
 */

const here = dirname(fileURLToPath(import.meta.url));
const HYDRATE = readFileSync(resolve(here, '../../../src/Services/Dcms.SiteBuilder/Runtime/hydrate.js'), 'utf8');

afterEach(() => {
  vi.unstubAllGlobals();
  document.head.innerHTML = '';
  document.body.innerHTML = '';
});

async function render(items: unknown[]): Promise<HTMLElement> {
  const el = document.createElement('div');
  el.setAttribute('data-dcms-component', 'VideoList');
  el.setAttribute('data-dcms-props', JSON.stringify({ layout: 'video' }));
  el.setAttribute(
    'data-dcms-bindings',
    JSON.stringify([{ propPath: 'items', instanceSlug: 'videos', query: { contentType: 'youtube-video' } }]),
  );
  document.body.appendChild(el);
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) =>
      new Response(JSON.stringify(url.startsWith('/api/videos/') ? { items } : {}), { status: 200 })),
  );
  new Function(HYDRATE)();
  await vi.waitFor(() => expect(el.getAttribute('data-dcms-hydrated')).toBe('true'));
  return el;
}

describe('hydrate.js video layout', () => {
  it('plays a YouTube video in the nocookie player', async () => {
    const el = await render([
      { slug: 'dQw4w9WgXcQ', data: { title: 'Launch day', url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ' } },
    ]);

    const frame = el.querySelector('iframe');
    expect(frame?.getAttribute('src')).toBe('https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?rel=0');
    expect(frame?.getAttribute('title')).toBe('Launch day');
    expect(el.querySelector('video')).toBeNull();
  });

  it('still plays a hosted file in a video tag', async () => {
    const el = await render([{ slug: 'clip', data: { title: 'Clip', source: '/media/clip.mp4' } }]);

    expect(el.querySelector('video')?.getAttribute('src')).toBe('/media/clip.mp4');
    expect(el.querySelector('iframe')).toBeNull();
  });
});
