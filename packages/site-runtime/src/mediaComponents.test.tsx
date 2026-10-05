// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { builtinRegistry } from './components';
import type { Node } from './document';
import { embedUrl, mapEmbedUrl, videoTarget } from './mediaComponents';
import { RenderNode } from './render';
import { RenderModeContext } from './renderMode';
import { checkVisualSite } from './validate';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const html = (node: Node, mode: 'live' | 'edit' = 'live') =>
  renderToStaticMarkup(
    <RenderModeContext.Provider value={mode}>
      <RenderNode node={node} registry={builtinRegistry} />
    </RenderModeContext.Provider>,
  );

describe('video', () => {
  it('understands the links people paste', () => {
    for (const url of ['https://www.youtube.com/watch?v=dQw4w9WgXcQ', 'https://youtu.be/dQw4w9WgXcQ?t=3', 'https://www.youtube.com/shorts/dQw4w9WgXcQ']) {
      expect(videoTarget('link', undefined, url)).toEqual({ kind: 'youtube', id: 'dQw4w9WgXcQ' });
    }
    expect(videoTarget('link', undefined, 'https://vimeo.com/76979871')).toEqual({ kind: 'vimeo', id: '76979871' });
    expect(videoTarget('link', undefined, 'https://example.com/video')).toBeNull();
  });

  it('plays privately on the site and is a still on the canvas', () => {
    const node: Node = { id: 'v', type: 'dcms.video', props: { url: 'https://youtu.be/dQw4w9WgXcQ' } };
    expect(html(node)).toContain('src="https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?rel=0"');
    expect(html(node, 'edit')).not.toContain('<iframe');
    expect(html(node, 'edit')).toContain('i.ytimg.com/vi/dQw4w9WgXcQ');
  });
});

describe('embed', () => {
  it('turns links from allowed services into their player address, and nothing else', () => {
    expect(embedUrl('https://open.spotify.com/intl-de/track/4uLU6hMCjMI75M1A2tKUQC')).toBe('https://open.spotify.com/embed/track/4uLU6hMCjMI75M1A2tKUQC');
    expect(embedUrl('https://docs.google.com/forms/d/e/abc/viewform')).toBe('https://docs.google.com/forms/d/e/abc/viewform?embedded=true');
    for (const bad of ['http://open.spotify.com/track/x', 'https://evil.example/embed', 'javascript:alert(1)', 'https://docs.google.com/document/d/x', 'https://open.spotify.com.evil.example/track/x']) {
      expect(embedUrl(bad), bad).toBeNull();
    }
  });

  it('is flagged when the link is not allowed, and renders nothing on the site', () => {
    const node: Node = { id: 'e', type: 'dcms.embed', props: { url: 'https://evil.example/x' } };
    expect(html(node)).toBe('<div class="dcms-node" data-dcms-node="e" data-dcms-type="dcms.embed"></div>');
    const json = (v: unknown) => `${JSON.stringify(v)}\n`;
    const problems = checkVisualSite({
      'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
      'dcms/pages/home.json': json({ schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'r', type: 'dcms.page', slots: { default: [node] } } }),
    });
    expect(problems.map((p) => p.message)).toEqual([expect.stringMatching(/^The embed link is not from an allowed service/)]);
  });
});

describe('map', () => {
  it('centres an OpenStreetMap embed on the pin', () => {
    expect(mapEmbedUrl(50, 14, 16)).toBe('https://www.openstreetmap.org/export/embed.html?bbox=13.99451,49.99725,14.00549,50.00275&layer=mapnik&marker=50.00000,14.00000');
  });
});

describe('gallery', () => {
  it('opens a picture full-screen, browses with the arrows and closes with Escape', async () => {
    const node: Node = {
      id: 'g',
      type: 'dcms.gallery',
      slots: {
        images: [
          { id: 'a', type: 'dcms.image', props: { src: '/a.jpg', alt: 'First' } },
          { id: 'b', type: 'dcms.image', props: { src: '/b.jpg', alt: 'Second' } },
        ],
      },
    };
    const el = document.createElement('div');
    document.body.appendChild(el);
    await act(async () => createRoot(el).render(<RenderNode node={node} registry={builtinRegistry} />));
    await act(async () => el.querySelectorAll('img')[1]!.click());
    expect(el.querySelector('.dcms-lightbox img')?.getAttribute('alt')).toBe('Second');
    await act(async () => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' })));
    expect(el.querySelector('.dcms-lightbox img')?.getAttribute('alt')).toBe('First');
    await act(async () => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })));
    expect(el.querySelector('.dcms-lightbox')).toBeNull();
    // Keyboard users reach the same viewer: each picture is a button.
    const first = el.querySelectorAll('img')[0]!;
    expect(first.getAttribute('role')).toBe('button');
    expect(first.tabIndex).toBe(0);
    await act(async () => first.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })));
    expect(el.querySelector('.dcms-lightbox img')?.getAttribute('alt')).toBe('First');
  });
});
