import { describe, expect, it } from 'vitest';
import type { App, Page } from './document';
import { routeFileName, withHead } from './vite';

describe('routeFileName', () => {
  it.each([
    ['/', 'index.html'],
    ['/about', 'about.html'],
    ['/about/team', 'about_team.html'],
    ['/events/:slug', 'events_@.html'],
    ['/a/:x/b', null],
  ])('%s → %s', (path, file) => {
    expect(routeFileName(path)).toBe(file);
  });
});

describe('withHead', () => {
  const app: App = { schemaVersion: 1, routes: [], seo: { titleTemplate: '%s · Acme', description: 'Default' } };
  const page: Page = {
    schemaVersion: 1,
    id: 'about',
    title: 'About',
    seo: { description: 'We make "things" <fast>', noIndex: true },
    root: { id: 'r', type: 'dcms.page' },
  };

  it('replaces the title and escapes what an author typed', () => {
    const html = withHead('<html><head><title>Site</title></head><body></body></html>', app, page);
    expect(html).toContain('<title>About · Acme</title>');
    expect(html).toContain('content="We make &quot;things&quot; &lt;fast&gt;"');
    expect(html).toContain('<meta name="robots" content="noindex"');
    expect(html).not.toContain('<title>Site</title>');
  });
});
