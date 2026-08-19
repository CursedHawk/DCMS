// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { applyNav, breadcrumbTrail, normalize } from './nav';

const routes = [
  { path: '/', title: 'Home' },
  { path: '/blog', title: 'Journal' },
  { path: '/blog/hello-world', title: 'Hello world' },
];

describe('normalize', () => {
  it('treats a trailing slash, an index file and a query as the same page', () => {
    expect(normalize('/blog/')).toBe('/blog');
    expect(normalize('/blog/index.html')).toBe('/blog');
    expect(normalize('/blog?page=2')).toBe('/blog');
    expect(normalize('/blog#top')).toBe('/blog');
  });

  it('keeps the root a root', () => {
    expect(normalize('/')).toBe('/');
    expect(normalize('')).toBe('/');
  });
});

describe('breadcrumbTrail', () => {
  it('is just home on the home page, and marks it current', () => {
    const trail = breadcrumbTrail('/', routes);
    expect(trail).toEqual([{ path: '/', label: 'Home', current: true }]);
  });

  it('names each level from the site’s own page titles', () => {
    expect(breadcrumbTrail('/blog/hello-world', routes).map((c) => c.label)).toEqual([
      'Home',
      'Journal',
      'Hello world',
    ]);
  });

  it('humanises a segment that has no page of its own', () => {
    // `/products` may exist only as a route prefix; a trail with a gap in it
    // reads as a bug, so the slug is used rather than skipped.
    const trail = breadcrumbTrail('/products/wide-gauge-widget', []);
    expect(trail.map((c) => c.label)).toEqual(['Home', 'Products', 'Wide gauge widget']);
  });

  it('marks only the last crumb as current', () => {
    const trail = breadcrumbTrail('/blog/hello-world', routes);
    expect(trail.map((c) => c.current)).toEqual([false, false, true]);
  });

  it('lets the author rename home', () => {
    expect(breadcrumbTrail('/about', [], 'Start')[0]!.label).toBe('Start');
  });
});

describe('applyNav', () => {
  const render = (html: string) => {
    const host = document.createElement('div');
    host.innerHTML = html;
    return host;
  };

  it('fills a breadcrumb bar with the trail for the page it is on', () => {
    const host = render(
      '<nav data-dcms-nav="breadcrumbs"><ol><li>placeholder</li></ol></nav>',
    );
    applyNav(host, '/blog/hello-world', routes);

    const items = Array.from(host.querySelectorAll('li')).map((li) => li.textContent);
    expect(items).toEqual(['Home', 'Journal', 'Hello world']);
    expect(host.querySelector('[aria-current="page"]')?.textContent).toBe('Hello world');
    // Only the last crumb is text; the rest have to be navigable.
    expect(host.querySelectorAll('a')).toHaveLength(2);
  });

  it('replaces the authored placeholder rather than appending to it', () => {
    const host = render('<nav data-dcms-nav="breadcrumbs"><ol><li>Old</li></ol></nav>');
    applyNav(host, '/', routes);
    expect(host.textContent).not.toContain('Old');
  });

  it('marks the current page in a menu', () => {
    const host = render(
      '<nav data-dcms-nav="menu"><a href="/">Home</a><a href="/blog">Blog</a></nav>',
    );
    applyNav(host, '/blog', routes);
    expect(host.querySelector('[aria-current="page"]')?.getAttribute('href')).toBe('/blog');
  });

  it('clears a stale mark, because the same markup is shown on every page', () => {
    const host = render('<nav data-dcms-nav="menu"><a href="/" aria-current="page">Home</a></nav>');
    applyNav(host, '/blog', routes);
    expect(host.querySelector('[aria-current="page"]')).toBeNull();
  });

  it('never marks an external link', () => {
    const host = render('<nav data-dcms-nav="menu"><a href="https://example.com/blog">X</a></nav>');
    applyNav(host, '/blog', routes);
    expect(host.querySelector('[aria-current="page"]')).toBeNull();
  });

  it('leaves markup that did not ask for it alone', () => {
    const host = render('<nav><a href="/blog">Blog</a></nav>');
    applyNav(host, '/blog', routes);
    expect(host.querySelector('[aria-current="page"]')).toBeNull();
  });

  it('escapes a crumb label rather than parsing it as markup', () => {
    const host = render('<nav data-dcms-nav="breadcrumbs"><ol></ol></nav>');
    applyNav(host, '/%3Cimg%20src=x%20onerror=alert(1)%3E', []);
    expect(host.querySelector('img')).toBeNull();
  });
});
