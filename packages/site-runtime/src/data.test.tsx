// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider, createMemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import { loadDocuments, siteRoutes } from './app';
import { builtinRegistry } from './components';
import { DataClientContext, mediaUrl, picture, type ContentItem, type DataClient } from './data';
import { pageSchema, type Node, type Page } from './document';
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
    expect(el.querySelector('img')?.getAttribute('src')).toBe('/api/media/11111111-1111-1111-1111-111111111111/webp-1280');
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

describe('dates bound to text', () => {
  it('read as dates in the site’s language, without moving a date-only value to another day', async () => {
    const { formatDate } = await import('./data');
    expect(formatDate('2026-07-01', 'en-GB')).toBe('1 Jul 2026');
    expect(formatDate('2026-07-01', 'cs')).toBe('1. 7. 2026');
    expect(formatDate('Lucerna', 'cs')).toBe('Lucerna');
    expect(formatDate('2026-07-01', 'not a locale!')).toBe('2026-07-01');
  });

  it('format only what a text prop shows; a url or date prop keeps the stored value', async () => {
    const { bindProps } = await import('./data');
    const scope = { item: { id: '1', slug: 's', data: { date: '2026-07-01' } }, index: 0, count: 1 };
    const defs = [
      { kind: 'text', name: 'label', label: 'L' },
      { kind: 'date', name: 'when', label: 'W' },
    ] as const;
    expect(bindProps({}, { label: 'date', when: 'date' }, defs as never, scope, 'en-GB')).toEqual({ label: '1 Jul 2026', when: '2026-07-01' });
  });
});

describe('external API connections as sources', () => {
  const source = { connection: 'tickets', operation: '/events?city=brno', items: 'data.events', slug: 'code' };
  const response = { data: { events: [{ id: 7, code: 'gala', name: 'Gala' }, { id: 8, code: 'jam', name: 'Jam' }, 'noise'] } };

  it('list the synced response’s items, keyed by the fields the source names', async () => {
    const node: Node = {
      id: 'list',
      type: 'dcms.collection',
      props: { source, limit: 1 },
      slots: { item: [{ id: 't', type: 'dcms.heading', bind: { text: 'name' } }] },
    };
    const data = client({ '/api/connections/tickets/events?city=brno': response });
    const el = await render(<RenderNode node={node} registry={builtinRegistry} />, data);
    expect([...el.querySelectorAll('h2')].map((h) => h.textContent)).toEqual(['Gala']);
  });

  it('give a detail page the list’s item with that slug, and 404 for one it does not have', async () => {
    const page: Page = { schemaVersion: 1, id: 'ev', title: 'Ev', data: { source, param: 'slug' }, root: { id: 'r', type: 'dcms.page', slots: { default: [{ id: 'h', type: 'dcms.heading', bind: { text: 'name' } }] } } };
    const documents = loadDocuments({ 'dcms/app.json': { schemaVersion: 1, routes: [{ id: 'ev', path: '/events/:slug', page: 'ev' }] }, 'dcms/pages/ev.json': page });
    const data = client({ '/api/connections/tickets/events?city=brno': response });
    const shown = await render(<RouterProvider router={createMemoryRouter(siteRoutes(documents), { initialEntries: ['/events/jam'] })} />, data);
    expect(shown.querySelector('h2')?.textContent).toBe('Jam');
    const missing = await render(<RouterProvider router={createMemoryRouter(siteRoutes(documents), { initialEntries: ['/events/nope'] })} />, data);
    expect(missing.textContent).toContain('Page not found');
  });

  it('are checked against the connections that exist, with any field allowed', () => {
    const json = (v: unknown) => `${JSON.stringify(v, null, 2)}\n`;
    const files = (src: unknown) => ({
      'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
      'dcms/pages/home.json': json({ schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'r', type: 'dcms.page', slots: { default: [
        { id: 'l', type: 'dcms.collection', props: { source: src }, slots: { item: [{ id: 't', type: 'dcms.heading', bind: { text: 'anything.at.all' } }] } },
      ] } } }),
    });
    const content = new Map([['connections/tickets/events?city=brno', new Set(['*'])]]);
    expect(checkVisualSite(files(source), undefined, content)).toEqual([]);
    expect(checkVisualSite(files({ ...source, connection: 'gone' }), undefined, content).map((p) => p.message)).toContain(
      'The collection shows connections/gone/events?city=brno, which this site’s plugins do not provide.',
    );
  });
});

describe('collection paging', () => {
  const posts = (from: number, n: number) =>
    Array.from({ length: n }, (_, i) => ({ id: String(from + i), slug: `p${from + i}`, data: { title: `Post ${from + i}` } }));
  const node = (paging: string): Node => ({
    id: 'list',
    type: 'dcms.collection',
    props: { source: { instance: 'news', contentType: 'post' }, limit: 2, paging },
    slots: { item: [{ id: 't', type: 'dcms.heading', bind: { text: 'title' } }] },
  });
  const titles = (el: HTMLElement) => [...el.querySelectorAll('h2')].map((h) => h.textContent);
  const click = async (el: HTMLElement, label: string) => {
    await act(async () => {
      [...el.querySelectorAll('button')].find((b) => b.textContent === label || b.getAttribute('aria-label') === label)!.click();
      await new Promise((r) => setTimeout(r, 0));
    });
  };

  it('“Load more” asks for a bigger page until everything is shown', async () => {
    const data = client({
      '/api/news/post?pageSize=2': { items: posts(1, 2), totalCount: 3 },
      '/api/news/post?pageSize=4': { items: posts(1, 3), totalCount: 3 },
    });
    const el = await render(<RenderNode node={node('more')} registry={builtinRegistry} />, data);
    expect(titles(el)).toEqual(['Post 1', 'Post 2']);
    await click(el, 'Load more');
    expect(titles(el)).toEqual(['Post 1', 'Post 2', 'Post 3']);
    expect(el.querySelector('.dcms-collection-more')).toBeNull();
  });

  it('page numbers fetch the page asked for', async () => {
    const data = client({
      '/api/news/post?pageSize=2': { items: posts(1, 2), totalCount: 3 },
      '/api/news/post?page=2&pageSize=2': { items: posts(3, 1), totalCount: 3 },
    });
    const el = await render(<RenderNode node={node('pages')} registry={builtinRegistry} />, data);
    expect(el.querySelector('.dcms-pager span')?.textContent).toBe('1 / 2');
    await click(el, 'Next page');
    expect(titles(el)).toEqual(['Post 3']);
    expect(el.querySelector('.dcms-pager span')?.textContent).toBe('2 / 2');
  });

  it('a connection pages over its snapshot without asking again', async () => {
    const data = client({ '/api/connections/tickets/events': posts(1, 5).map((p) => ({ id: p.id, slug: p.slug, title: p.data.title })) });
    const conn: Node = { ...node('pages'), props: { source: { connection: 'tickets', operation: '/events' }, limit: 2, paging: 'pages' } };
    const el = await render(<RenderNode node={conn} registry={builtinRegistry} />, data);
    expect(titles(el)).toEqual(['Post 1', 'Post 2']);
    await click(el, 'Next page');
    await click(el, 'Next page');
    expect(titles(el)).toEqual(['Post 5']);
    expect(data.calls).toEqual(['/api/connections/tickets/events']);
  });
});

describe('form fields', () => {
  it('offers choices as a dropdown, radios or a checkbox group, and posts a group as a list', async () => {
    const posted: unknown[] = [];
    const data: DataClient = { get: async () => ({}), post: async (_p, body) => (posted.push(body), { ok: true }) };
    const form: Node = {
      id: 'f',
      type: 'dcms.form',
      props: { instance: 'forms', form: 'survey' },
      slots: {
        fields: [
          { id: 'a', type: 'dcms.field', props: { name: 'size', label: 'Size', type: 'select', options: 'S\nM\nL' } },
          { id: 'b', type: 'dcms.field', props: { name: 'days', label: 'Days', type: 'checkboxes', options: 'Mon\nTue\nWed' } },
          { id: 'c', type: 'dcms.field', props: { name: 'ok', label: 'OK', type: 'checkbox' } },
        ],
      },
    };
    const el = await render(<RenderNode node={form} registry={builtinRegistry} />, data);
    expect([...el.querySelectorAll('select option')].map((o) => o.textContent)).toEqual(['Choose…', 'S', 'M', 'L']);
    (el.querySelector('select') as HTMLSelectElement).value = 'M';
    const boxes = el.querySelectorAll<HTMLInputElement>('fieldset input[type="checkbox"]');
    boxes[0]!.checked = true;
    boxes[2]!.checked = true;
    await act(async () => {
      el.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
      await new Promise((r) => setTimeout(r, 0));
    });
    expect(posted).toEqual([{ size: 'M', days: ['Mon', 'Wed'], ok: false }]);
  });

  it('is checked: one name per field in a form, and choices that have options', () => {
    const json = (v: unknown) => `${JSON.stringify(v)}\n`;
    const problems = checkVisualSite({
      'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
      'dcms/pages/home.json': json({
        schemaVersion: 1,
        id: 'home',
        title: 'Home',
        root: {
          id: 'r',
          type: 'dcms.page',
          slots: {
            default: [
              {
                id: 'f',
                type: 'dcms.form',
                props: { instance: 'forms', form: 'x' },
                slots: { fields: [{ id: 'a', type: 'dcms.field', props: { name: 'email' } }, { id: 'b', type: 'dcms.field', props: { name: 'email', type: 'radio', options: ' ' } }] },
              },
            ],
          },
        },
      }),
    }).map((p) => p.message);
    expect(problems).toEqual(
      expect.arrayContaining([
        '2 fields of this form are named “email”; each needs its own name, or one answer overwrites the other.',
        'This choice field has no options to choose from.',
      ]),
    );
  });
});

describe('pictures', () => {
  const A = '11111111-1111-1111-1111-111111111111';
  const B = '22222222-2222-2222-2222-222222222222';

  it('loads a library picture as WebP at the width the layout needs, anything else as given', () => {
    expect(picture(A)).toEqual({
      src: `/api/media/${A}/webp-1280`,
      srcSet: [320, 640, 1280, 1920].map((w) => `/api/media/${A}/webp-${w} ${w}w`).join(', '),
    });
    expect(picture(`/api/media/${A}/original`, 1920)?.src).toBe(`/api/media/${A}/webp-1920`);
    // A variant chosen on purpose, an external picture, a site file: untouched.
    expect(picture(`/api/media/${A}/webp-640`)).toEqual({ src: `/api/media/${A}/webp-640` });
    expect(picture('https://example.com/a.jpg')).toEqual({ src: 'https://example.com/a.jpg' });
  });

  it('an image keeps its original file when asked to', async () => {
    const el = await render(<RenderNode node={{ id: 'i', type: 'dcms.image', props: { src: `/api/media/${A}/original`, alt: 'x', file: 'original' } }} registry={builtinRegistry} />, client({}));
    expect(el.querySelector('img')?.getAttribute('src')).toBe(`/api/media/${A}/original`);
    expect(el.querySelector('img')?.hasAttribute('srcset')).toBe(false);
  });

  it('a gallery on a detail page shows every photo of the item, sized to its columns', async () => {
    const page: Page = {
      schemaVersion: 1,
      id: 'event',
      title: 'Event',
      data: { source: { instance: 'events', contentType: 'event' }, param: 'slug' },
      root: {
        id: 'r',
        type: 'dcms.page',
        slots: {
          default: [
            { id: 'g', type: 'dcms.gallery', props: { columns: '4' }, bind: { images: 'photos', alt: 'title' } },
            // One photo of the list on its own, by its position.
            { id: 'cover', type: 'dcms.image', bind: { src: 'photos.1' } },
          ],
        },
      },
    };
    expect(pageSchema.safeParse(page).success).toBe(true);
    // The validator knows `photos.1` is the field `photos`.
    const files = { 'dcms/app.json': JSON.stringify({ schemaVersion: 1, routes: [{ id: 'event', path: '/events/:slug', page: 'event' }] }), 'dcms/pages/event.json': JSON.stringify(page) };
    const known = new Map([['events/event', new Set(['title', 'photos'])]]);
    expect(checkVisualSite(files, builtinRegistry, known).filter((p) => p.file.startsWith('dcms/pages'))).toEqual([]);
    const documents = loadDocuments({
      'dcms/app.json': { schemaVersion: 1, routes: [{ id: 'event', path: '/events/:slug', page: 'event' }] },
      'dcms/pages/event.json': page,
    });
    const data = client({ '/api/events/event/summer': { id: '1', slug: 'summer', data: { title: 'Summer party', photos: [A, { id: B }] } } });
    const el = await render(<RouterProvider router={createMemoryRouter(siteRoutes(documents), { initialEntries: ['/events/summer'] })} />, data);
    const shots = [...el.querySelectorAll('.dcms-gallery img')];
    expect(shots.map((i) => i.getAttribute('src'))).toEqual([`/api/media/${A}/webp-1280`, `/api/media/${B}/webp-1280`]);
    expect(shots.map((i) => i.getAttribute('alt'))).toEqual(['Summer party (1/2)', 'Summer party (2/2)']);
    expect(shots[0]!.getAttribute('sizes')).toBe('(max-width: 640px) 50vw, 25vw');
    expect(el.querySelector('img.dcms-image:not(.dcms-gallery img)')?.getAttribute('src')).toBe(`/api/media/${B}/webp-1280`);
  });
});
