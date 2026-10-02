// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider, createMemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import { loadDocuments, siteRoutes } from './app';
import { builtinRegistry } from './components';
import { DataClientContext, mediaUrl, type ContentItem, type DataClient } from './data';
import type { Node, Page } from './document';
import { pageHead } from './head';
import { RenderNode } from './render';
import { checkVisualSite } from './validate';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const items: ContentItem[] = [
  { id: '1', slug: 'summer', data: { title: 'Summer party', cover: '11111111-1111-1111-1111-111111111111' } },
  { id: '2', slug: 'winter', data: { title: 'Winter ball' } },
];

function client(routes: Record<string, unknown>): DataClient & { calls: string[] } {
  const calls: string[] = [];
  return {
    calls,
    async get(path) {
      calls.push(path);
      if (!(path in routes)) throw new Error(`404 ${path}`);
      return routes[path];
    },
    async post() {
      return undefined;
    },
  };
}

async function render(ui: React.ReactNode, data: DataClient): Promise<HTMLElement> {
  const el = document.createElement('div');
  await act(async () => {
    createRoot(el).render(<DataClientContext.Provider value={data}>{ui}</DataClientContext.Provider>);
  });
  // Let the fetches resolve and re-render.
  await act(async () => {
    await new Promise((r) => setTimeout(r, 0));
  });
  return el;
}

const card: Node = {
  id: 'card',
  type: 'dcms.stack',
  slots: {
    default: [
      { id: 't', type: 'dcms.heading', bind: { text: 'title' } },
      { id: 'img', type: 'dcms.image', bind: { src: 'cover' }, props: { alt: 'cover' } },
      { id: 'b', type: 'dcms.button', props: { label: 'Details' }, action: { type: 'navigate', to: '/events/:slug' } },
    ],
  },
};

const collection = (extra: Partial<Node> = {}): Node => ({
  id: 'list',
  type: 'dcms.collection',
  props: { source: { instance: 'events', contentType: 'event' }, limit: 2 },
  slots: {
    item: [card],
    empty: [{ id: 'e', type: 'dcms.text', props: { text: 'Nothing yet' } }],
    error: [{ id: 'x', type: 'dcms.text', props: { text: 'Could not load' } }],
  },
  ...extra,
});

describe('Collection', () => {
  it('repeats the item template, binding each item and filling :slug in links', async () => {
    const data = client({ '/api/events/event?pageSize=2': { items, totalCount: 2 } });
    const el = await render(<RenderNode node={collection()} registry={builtinRegistry} />, data);
    expect([...el.querySelectorAll('h2')].map((h) => h.textContent)).toEqual(['Summer party', 'Winter ball']);
    expect([...el.querySelectorAll('a')].map((a) => a.getAttribute('href'))).toEqual(['/events/summer', '/events/winter']);
    expect(el.querySelector('img')?.getAttribute('src')).toBe('/api/media/11111111-1111-1111-1111-111111111111/original');
    expect(data.calls).toEqual(['/api/events/event?pageSize=2']);
  });

  it('shows its empty state, and its error state when loading fails', async () => {
    const empty = await render(<RenderNode node={collection()} registry={builtinRegistry} />, client({ '/api/events/event?pageSize=2': { items: [], totalCount: 0 } }));
    expect(empty.textContent).toBe('Nothing yet');
    const failed = await render(<RenderNode node={collection()} registry={builtinRegistry} />, client({}));
    expect(failed.textContent).toBe('Could not load');
  });
});

describe('detail pages', () => {
  const page: Page = {
    schemaVersion: 1,
    id: 'event',
    title: 'Event',
    seo: { title: '{title}' },
    data: { source: { instance: 'events', contentType: 'event' }, param: 'slug' },
    root: { id: 'r', type: 'dcms.page', slots: { default: [{ id: 'h', type: 'dcms.heading', bind: { text: 'title' } }] } },
  };
  const documents = loadDocuments({
    'dcms/app.json': { schemaVersion: 1, routes: [{ id: 'event', path: '/events/:slug', page: 'event' }] },
    'dcms/pages/event.json': page,
  });

  it('shows the item the address names, and 404s for one that does not exist', async () => {
    const data = client({ '/api/events/event/summer': items[0] });
    const shown = await render(<RouterProvider router={createMemoryRouter(siteRoutes(documents), { initialEntries: ['/events/summer'] })} />, data);
    expect(shown.querySelector('h2')?.textContent).toBe('Summer party');
    const missing = await render(<RouterProvider router={createMemoryRouter(siteRoutes(documents), { initialEntries: ['/events/nope'] })} />, data);
    expect(missing.textContent).toContain('Page not found');
  });

  it('fills the head from the item, and leaves only static words without one', () => {
    const app = { schemaVersion: 1 as const, routes: [], seo: { titleTemplate: '%s · Acme' } };
    expect(pageHead(app, page, { item: items[0]!, index: 0, count: 1 }).title).toBe('Summer party · Acme');
    expect(pageHead(app, page).title).toBe('Event · Acme');
  });
});

describe('mediaUrl', () => {
  it('shows a list of media (an event’s photos) by its first, and nothing for an empty one', () => {
    expect(mediaUrl(['11111111-1111-1111-1111-111111111111', '/b.jpg'])).toBe('/api/media/11111111-1111-1111-1111-111111111111/original');
    expect(mediaUrl([])).toBeUndefined();
  });
});

describe('RichText', () => {
  it('strips scripts and handlers before anything reaches the DOM', async () => {
    const node: Node = { id: 'r', type: 'dcms.richtext', props: { html: '<p>ok<img src=x onerror="alert(1)"><script>alert(2)</script><a href="javascript:alert(3)">x</a></p>' } };
    const el = await render(<RenderNode node={node} registry={builtinRegistry} />, client({}));
    const html = el.querySelector('.dcms-richtext')!.innerHTML;
    expect(html).toContain('<p>ok');
    expect(html).not.toMatch(/onerror|<script|javascript:/);
  });
});

describe('checkVisualSite and data', () => {
  const json = (v: unknown) => `${JSON.stringify(v, null, 2)}\n`;
  const site = (children: Node[], extra: Record<string, string> = {}) => ({
    'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
    'dcms/pages/home.json': json({ schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'r', type: 'dcms.page', slots: { default: children } } }),
    ...extra,
  });
  const content = new Map([['events/event', new Set(['title', 'cover'])]]);

  it('refuses a binding with no item around it', () => {
    const problems = checkVisualSite(site([{ id: 'h', type: 'dcms.heading', bind: { text: 'title' } }]));
    expect(problems[0]?.message).toMatch(/nothing around it provides an item/);
  });

  it('checks bound fields and sources against what the plugins offer', () => {
    const bad = collection();
    (bad.slots!.item![0]!.slots!.default![0]!.bind as Record<string, string>).text = 'headline';
    const problems = checkVisualSite(site([bad]), undefined, content).map((p) => p.message);
    expect(problems).toContain('Heading › Text shows the field “headline”, which events/event does not have.');
    const unknown = checkVisualSite(site([collection({ props: { source: { instance: 'shop', contentType: 'product' } } })]), undefined, content);
    expect(unknown.map((p) => p.message)).toContain('The collection shows shop/product, which this site’s plugins do not provide.');
  });

  it('wants a detail page’s route to carry its parameter', () => {
    const files = {
      'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }, { id: 'ev', path: '/events', page: 'ev' }] }),
      'dcms/pages/home.json': json({ schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'r', type: 'dcms.page' } }),
      'dcms/pages/ev.json': json({ schemaVersion: 1, id: 'ev', title: 'Ev', data: { source: { instance: 'events', contentType: 'event' }, param: 'slug' }, root: { id: 'r', type: 'dcms.page' } }),
    };
    expect(checkVisualSite(files).map((p) => p.message)).toContain('The route /events shows a detail page, so it needs “:slug” in it.');
  });
});

describe('Form', () => {
  it('posts what the visitor entered to the Forms plugin and thanks them', async () => {
    const posted: { path: string; body: unknown }[] = [];
    const data: DataClient = {
      get: async () => ({}),
      post: async (path, body) => {
        posted.push({ path, body });
        return { ok: true };
      },
    };
    const form: Node = {
      id: 'f',
      type: 'dcms.form',
      props: { instance: 'forms', form: 'contact', successMessage: 'Got it' },
      slots: {
        fields: [
          { id: 'n', type: 'dcms.field', props: { name: 'name', label: 'Name' } },
          { id: 'c', type: 'dcms.field', props: { name: 'consent', label: 'OK to reply', type: 'checkbox' } },
        ],
      },
    };
    const el = await render(<RenderNode node={form} registry={builtinRegistry} />, data);
    (el.querySelector('input[name="name"]') as HTMLInputElement).value = 'Ada';
    (el.querySelector('input[name="consent"]') as HTMLInputElement).checked = true;
    await act(async () => {
      el.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
      await new Promise((r) => setTimeout(r, 0));
    });
    expect(posted).toEqual([{ path: '/api/forms/forms/contact', body: { name: 'Ada', consent: true } }]);
    expect(el.querySelector('[role="status"]')?.textContent).toBe('Got it');
  });
});
