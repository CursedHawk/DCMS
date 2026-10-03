import { describe, expect, it } from 'vitest';
import { loadDocuments } from './app';
import { prerender } from './server';

describe('prerendering', () => {
  const documents = loadDocuments({
    'dcms/app.json': {
      schemaVersion: 1,
      routes: [
        { id: 'home', path: '/', page: 'home' },
        { id: 'about', path: '/about', page: 'home' },
        { id: 'event', path: '/events/:slug', page: 'event' },
      ],
    },
    'dcms/pages/home.json': {
      schemaVersion: 1,
      id: 'home',
      title: 'Home',
      root: { id: 'r', type: 'dcms.page', slots: { default: [{ id: 'h', type: 'dcms.heading', props: { text: 'Hello crawler' } }, { id: 't', type: 'dcms.richtext', props: { html: '<p>x</p>' } }] } },
    },
    'dcms/pages/event.json': {
      schemaVersion: 1,
      id: 'event',
      title: 'Event',
      data: { source: { instance: 'events', contentType: 'gig' }, param: 'slug' },
      root: { id: 'r2', type: 'dcms.page' },
    },
  });

  it('renders every route without parameters to its own file, and leaves the rest to the browser', () => {
    const pages = prerender(documents);
    expect(pages.map((p) => p.fileName)).toEqual(['index.html', 'about.html']);
    expect(pages[0]!.html).toContain('Hello crawler');
    // Rich text is sanitised in the browser only; the server leaves its box empty to match.
    expect(pages[0]!.html).toContain('<div class="dcms-richtext"></div>');
  });
});
