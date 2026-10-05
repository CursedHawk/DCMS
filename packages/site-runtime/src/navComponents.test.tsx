// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { renderToStaticMarkup } from 'react-dom/server';
import { RouterProvider, createMemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import { loadDocuments, siteRoutes } from './app';
import { builtinRegistry } from './components';
import { crumbsFor, footerNote } from './navComponents';
import { RenderNode } from './render';
import { SiteContext } from './site';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

describe('breadcrumbs', () => {
  const app = {
    routes: [{ path: '/' }, { path: '/events' }, { path: '/events/:slug' }],
    navigation: { main: [{ label: 'What’s on', to: '/events' }] },
  };
  it('walk the address using menu labels, page titles and the item’s own title', () => {
    expect(crumbsFor('/events/summer-jam', app, { '/events': 'Events', '/events/:slug': 'Event' }, 'Summer Jam')).toEqual([
      { label: 'Home', to: '/' },
      { label: 'What’s on', to: '/events' },
      { label: 'Summer Jam', to: undefined },
    ]);
    expect(crumbsFor('/', app, {})).toEqual([{ label: 'Home' }]);
  });
});

describe('footer', () => {
  it('keeps the year current', () => {
    expect(footerNote('© {year} Acme', new Date('2031-02-01'))).toBe('© 2031 Acme');
  });
});

describe('section anchors', () => {
  it('give the section an id a link can jump to', () => {
    expect(renderToStaticMarkup(<RenderNode node={{ id: 's', type: 'dcms.section', props: { anchor: 'Our Pricing!' } }} registry={builtinRegistry} />)).toContain('<section id="our-pricing"');
  });
});

describe('navigation', () => {
  it('marks the current page, and folds behind a menu button that opens and closes', async () => {
    const documents = loadDocuments({
      'dcms/app.json': {
        schemaVersion: 1,
        routes: [{ id: 'home', path: '/', page: 'home' }, { id: 'about', path: '/about', page: 'home' }],
        navigation: { main: [{ label: 'Home', to: '/' }, { label: 'About', to: '/about' }] },
        shell: { id: 'sh', type: 'dcms.page', slots: { default: [{ id: 'n', type: 'dcms.nav' }, { id: 'o', type: 'dcms.outlet' }] } },
      },
      'dcms/pages/home.json': { schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'r', type: 'dcms.page' } },
    });
    const el = document.createElement('div');
    await act(async () =>
      createRoot(el).render(
        <SiteContext.Provider value={{ app: documents.app }}>
          <RouterProvider router={createMemoryRouter(siteRoutes(documents), { initialEntries: ['/about'] })} />
        </SiteContext.Provider>,
      ),
    );
    expect(el.querySelector('[aria-current="page"]')?.textContent).toBe('About');
    const toggle = el.querySelector<HTMLButtonElement>('.dcms-nav-toggle')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    await act(async () => toggle.click());
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(el.querySelector('.dcms-nav')!.className).toContain('dcms-nav-open');
  });
});
