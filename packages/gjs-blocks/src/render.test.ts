// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { expandSnippet, renderTemplate } from './render';
import type { PreviewItem } from './preview';

/**
 * The template renderer is what makes a tenant component a component rather than
 * a snippet with a nice name, so these are the cases that decide whether an
 * author's own layout survives contact with their own data: a repeat that
 * actually repeats, a field name the built-in guesses would never find, a slot
 * with nothing in it, and a value that must not become markup.
 */

const items: PreviewItem[] = [
  { id: '1', slug: 'first', data: { nadpis: 'První', perex: 'Text jedna', obrazek: '/a.png' } },
  { id: '2', slug: 'second', data: { nadpis: 'Druhý', perex: 'Text dva' } },
];

const render = (template: string, list: PreviewItem[] = items, props = {}) =>
  renderTemplate(document, template, list, props);

describe('renderTemplate', () => {
  it('repeats the marked subtree once per item', () => {
    const html = render(
      '<div class="wrap"><article data-dcms-repeat><h3 data-dcms-bind="text:nadpis"></h3></article></div>',
    );
    expect(html).toContain('První');
    expect(html).toContain('Druhý');
    expect(html.match(/<article/g)).toHaveLength(2);
  });

  it('gives each repeated item its own values', () => {
    // The bug this guards: a second pass re-binding the clones against the
    // ambient item, so every card shows the first item's content.
    const html = render('<ul><li data-dcms-repeat data-dcms-bind="text:nadpis"></li></ul>');
    expect(html).toBe('<ul><li>První</li><li>Druhý</li></ul>');
  });

  it('reads a tenant field the built-in guesses would never find', () => {
    const html = render('<h1 data-dcms-bind="text:nadpis"></h1>', [items[0]!]);
    expect(html).toBe('<h1>První</h1>');
  });

  it('reads a nested custom-field value by dotted path and by bare name', () => {
    const item: PreviewItem = { data: { title: 'T', values: { barva: 'modrá' } } };
    expect(render('<p data-dcms-bind="text:values.barva"></p>', [item])).toBe('<p>modrá</p>');
    expect(render('<p data-dcms-bind="text:barva"></p>', [item])).toBe('<p>modrá</p>');
  });

  it('reads props with @ and the item envelope with #', () => {
    const html = renderTemplate(
      document,
      '<div><h2 data-dcms-bind="text:@heading"></h2><a data-dcms-bind="href:#slug"></a></div>',
      [items[0]!],
      { heading: 'Latest' },
    );
    expect(html).toContain('<h2>Latest</h2>');
    expect(html).toContain('href="first"');
  });

  it('keeps a field and a prop of the same name apart', () => {
    const item: PreviewItem = { data: { heading: 'from the item' } };
    const html = renderTemplate(
      document,
      '<div><p data-dcms-bind="text:heading"></p><p data-dcms-bind="text:@heading"></p></div>',
      [item],
      { heading: 'from the prop' },
    );
    expect(html).toContain('<p>from the item</p>');
    expect(html).toContain('<p>from the prop</p>');
  });

  it('wraps a value with prefix and suffix, for building a route from a slug', () => {
    const html = render(
      '<a data-dcms-repeat data-dcms-bind="href:#slug" data-dcms-prefix="/blog/" data-dcms-suffix=".html">x</a>',
    );
    expect(html).toContain('href="/blog/first.html"');
  });

  it('drops an element whose data-dcms-if source is empty', () => {
    const html = render(
      '<ul><li data-dcms-repeat><img data-dcms-if="obrazek" data-dcms-bind="src:obrazek" /></li></ul>',
    );
    expect(html.match(/<img/g)).toHaveLength(1);
    expect(html).toContain('src="/a.png"');
  });

  it('keeps the authored content when a binding resolves to nothing', () => {
    // An empty field must not blank a button that already says something useful.
    const html = render('<a data-dcms-bind="text:missing">Read more</a>', [items[0]!]);
    expect(html).toBe('<a>Read more</a>');
  });

  it('uses data-dcms-fallback when the field is empty', () => {
    const html = render('<p data-dcms-bind="text:missing" data-dcms-fallback="Untitled"></p>', [
      items[0]!,
    ]);
    expect(html).toBe('<p>Untitled</p>');
  });

  it('shows the empty marker only when nothing was fetched', () => {
    const template =
      '<div><p data-dcms-empty>Nothing yet</p><ul><li data-dcms-repeat data-dcms-bind="text:nadpis"></li></ul></div>';
    expect(render(template, [])).toContain('Nothing yet');
    expect(render(template)).not.toContain('Nothing yet');
  });

  it('resolves an asset id in a src to the media endpoint', () => {
    const item: PreviewItem = { data: { cover: 'a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d' } };
    expect(render('<img data-dcms-bind="src:cover" />', [item])).toContain(
      'src="/api/media/a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d/original"',
    );
  });

  it('lets the caller rewrite media URLs, which is how the canvas shows them', () => {
    const item: PreviewItem = { data: { cover: '/a.png' } };
    const html = renderTemplate(document, '<img data-dcms-bind="src:cover" />', [item], {}, {
      onMedia: (url) => `blob:${url}`,
    });
    expect(html).toContain('src="blob:/a.png"');
  });

  it('sets a background image without letting the value escape the url()', () => {
    const item: PreviewItem = { data: { cover: '/a".png' } };
    const html = render('<div data-dcms-bind="style:background-image:cover"></div>', [item]);
    expect(html).toContain('background-image');
    expect(html).not.toContain('/a".png');
  });

  it('renders a value as text, not as markup', () => {
    const item: PreviewItem = { data: { title: '<script>alert(1)</script>' } };
    const html = render('<p data-dcms-bind="text:title"></p>', [item]);
    expect(html).not.toContain('<script>');
    expect(html).toContain('&lt;script&gt;');
  });

  it('renders markup only where the author opted in', () => {
    const item: PreviewItem = { data: { body: '<em>yes</em>' } };
    expect(render('<div data-dcms-bind="html:body"></div>', [item])).toContain('<em>yes</em>');
  });

  it('formats a date in a text slot and leaves one in an href alone', () => {
    const item: PreviewItem = { data: { publishedAt: '2025-03-04T00:00:00Z' } };
    expect(render('<span data-dcms-bind="text:publishedAt"></span>', [item])).not.toContain('2025-03-04T');
    expect(render('<a data-dcms-bind="href:publishedAt"></a>', [item])).toContain('2025-03-04T');
  });

  it('trims text to data-dcms-truncate characters', () => {
    const item: PreviewItem = { data: { body: 'one two three four five six seven eight' } };
    const html = render('<p data-dcms-bind="text:body" data-dcms-truncate="12"></p>', [item]);
    expect(html).toMatch(/…<\/p>$/);
    expect(html.length).toBeLessThan(30);
  });

  it('leaves no binding attributes in the output', () => {
    const html = render(
      '<div data-dcms-bind="title:nadpis"><p data-dcms-repeat data-dcms-bind="text:perex" data-dcms-truncate="5"></p></div>',
    );
    expect(html).not.toContain('data-dcms-');
  });

  it('expands a snippet against its props alone', () => {
    expect(expandSnippet(document, '<h2 data-dcms-bind="text:@title"></h2>', { title: 'Hi' })).toBe(
      '<h2>Hi</h2>',
    );
  });
});
