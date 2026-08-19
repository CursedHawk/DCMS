import { describe, expect, it } from 'vitest';
import {
  escapeHtml,
  fieldsOf,
  layoutOf,
  mediaUrl,
  previewBody,
  previewHtml,
  previewNotice,
  readField,
  type PreviewItem,
} from './preview';

const post = (over: Record<string, unknown> = {}): PreviewItem => ({
  id: '1',
  data: { title: 'Hello', excerpt: 'World', coverImage: 'a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d', ...over },
});

describe('escapeHtml', () => {
  it('escapes every character that could break out of markup', () => {
    expect(escapeHtml(`<a href="x">&'`)).toBe('&lt;a href=&quot;x&quot;&gt;&amp;&#39;');
  });

  it('renders null and undefined as nothing', () => {
    expect(escapeHtml(null)).toBe('');
    expect(escapeHtml(undefined)).toBe('');
  });
});

describe('fieldsOf', () => {
  it('reads the delivery API envelope', () => {
    expect(fieldsOf({ data: { title: 'x' } })).toEqual({ title: 'x' });
  });

  it('reads the admin API envelope', () => {
    expect(fieldsOf({ draft: { title: 'x' } })).toEqual({ title: 'x' });
  });

  it('falls back to a bare object', () => {
    expect(fieldsOf({ title: 'x' })).toMatchObject({ title: 'x' });
  });
});

describe('mediaUrl', () => {
  it('turns a bare asset id into the delivery URL', () => {
    expect(mediaUrl('a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d')).toBe(
      '/api/media/a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d/original',
    );
  });

  it('leaves an existing URL alone', () => {
    expect(mediaUrl('https://cdn.example/x.png')).toBe('https://cdn.example/x.png');
  });

  it('takes the first of an array', () => {
    expect(mediaUrl(['/a.png', '/b.png'])).toBe('/a.png');
  });

  it('digs a URL out of an object', () => {
    expect(mediaUrl({ url: '/a.png' })).toBe('/a.png');
    expect(mediaUrl({ id: 'a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d' })).toContain('/api/media/');
  });

  it('returns nothing for nothing', () => {
    expect(mediaUrl(null)).toBe('');
    expect(mediaUrl([])).toBe('');
  });
});

describe('layoutOf', () => {
  it('honours a known layout', () => {
    expect(layoutOf({ layout: 'article' })).toBe('article');
  });

  it('falls back for an unknown or absent one', () => {
    expect(layoutOf({ layout: 'spiral' })).toBe('cards');
    expect(layoutOf({})).toBe('cards');
    expect(layoutOf({}, 'list')).toBe('list');
  });
});

describe('previewBody', () => {
  it('shows the empty state when there is nothing to show', () => {
    expect(previewBody([], {})).toContain('No content published yet.');
  });

  it('uses the author’s own empty message', () => {
    expect(previewBody([], { emptyText: 'Nothing here yet' })).toContain('Nothing here yet');
  });

  it('escapes the empty message', () => {
    expect(previewBody([], { emptyText: '<script>' })).toContain('&lt;script&gt;');
  });

  describe('cards', () => {
    const html = previewBody([post()], { layout: 'cards' });

    it('draws one card per item', () => {
      expect(html).toContain('dcms-collection');
      expect(html).toContain('dcms-card-title');
    });

    it('resolves the image to a delivery URL', () => {
      expect(html).toContain('/api/media/a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d/original');
    });

    it('wraps the card in a link when the item has one', () => {
      expect(previewBody([post({ linkUrl: '/posts/hello' })], {})).toContain('href="/posts/hello"');
    });

    it('leaves a card unlinked otherwise', () => {
      expect(html).not.toContain('<a href');
    });
  });

  describe('list', () => {
    it('draws rows rather than cards', () => {
      const html = previewBody([post()], { layout: 'list' });
      expect(html).toContain('dcms-row-title');
      expect(html).not.toContain('dcms-card');
    });

    it('labels an item with no title', () => {
      expect(previewBody([{ data: {} }], { layout: 'list' })).toContain('(untitled)');
    });
  });

  describe('article', () => {
    it('draws only the first item', () => {
      const html = previewBody([post(), post({ title: 'Second' })], { layout: 'article' });
      expect(html).toContain('Hello');
      expect(html).not.toContain('Second');
    });
  });

  describe('media layouts', () => {
    const track = { data: { title: 'Song', source: '/a.mp3' } };

    it('draws video players', () => {
      expect(previewBody([track], { layout: 'video' })).toContain('<video');
    });

    it('draws audio players', () => {
      expect(previewBody([track], { layout: 'audio' })).toContain('<audio');
    });

    it('draws download links', () => {
      const html = previewBody([track], { layout: 'downloads' });
      expect(html).toContain('dcms-download');
      expect(html).toContain('download>');
    });
  });

  describe('tiles', () => {
    it('draws an image with the title burned in', () => {
      const html = previewBody([post()], { layout: 'tiles' });
      expect(html).toContain('dcms-tile-caption');
      expect(html).toContain('/api/media/');
    });

    it('makes the whole tile the link when the item has one', () => {
      const html = previewBody([post({ linkUrl: '/p/1' })], { layout: 'tiles' });
      expect(html).toContain('<a class="dcms-tile" href="/p/1"');
    });
  });

  describe('compact', () => {
    it('puts a thumbnail beside the title', () => {
      const html = previewBody([post()], { layout: 'compact' });
      expect(html).toContain('dcms-row-media');
      expect(html).toContain('dcms-row-title');
    });
  });

  describe('feature', () => {
    it('draws a band per item rather than a grid', () => {
      const html = previewBody([post(), post()], { layout: 'feature' });
      expect(html).not.toContain('dcms-collection');
      expect(html.match(/dcms-feature-item/g)).toHaveLength(2);
    });
  });

  describe('arrangement and columns', () => {
    it('carries an explicit column count onto the collection', () => {
      expect(previewBody([post()], { columns: '3' })).toContain('data-columns="3"');
    });

    it('says nothing when the columns should just fit', () => {
      expect(previewBody([post()], {})).not.toContain('data-columns');
    });

    it('marks a non-grid arrangement', () => {
      expect(previewBody([post()], { arrangement: 'masonry' })).toContain('data-layout="masonry"');
      expect(previewBody([post()], { arrangement: 'grid' })).not.toContain('data-layout');
    });
  });

  describe('meta and tags', () => {
    it('formats a date rather than printing the timestamp', () => {
      const html = previewBody([post({ publishedAt: '2025-03-04T10:00:00Z' })], {});
      expect(html).toContain('dcms-card-meta');
      expect(html).not.toContain('2025-03-04T10:00:00Z');
    });

    it('leaves a non-date meta value alone', () => {
      expect(previewBody([post({ category: 'Releases' })], { metaField: 'category' })).toContain(
        'Releases',
      );
    });

    it('draws tags as chips', () => {
      const html = previewBody([post({ tags: ['a', 'b'] })], {});
      expect(html.match(/dcms-tag"/g)).toHaveLength(2);
    });

    it('splits a comma-separated tag field', () => {
      const html = previewBody([post({ tags: 'a, b, c' })], {});
      expect(html.match(/dcms-tag"/g)).toHaveLength(3);
    });
  });

  describe('excerpt trimming', () => {
    it('cuts at a word and marks the cut', () => {
      const item = { data: { title: 'x', excerpt: 'one two three four five six seven' } };
      const html = previewBody([item], { excerptLength: 12 });
      expect(html).toContain('…');
      expect(html).not.toContain('seven');
    });

    it('leaves short text alone', () => {
      expect(previewBody([{ data: { excerpt: 'short' } }], { excerptLength: 40 })).toContain('short');
    });
  });

  describe('field mapping', () => {
    const item: PreviewItem = { data: { nadpis: 'Ahoj', perex: 'Světe', obrazek: '/o.png' } };

    it('guesses nothing when the fields are not conventionally named', () => {
      const html = previewBody([item], {});
      expect(html).not.toContain('Ahoj');
    });

    it('uses the author’s explicit mapping', () => {
      const html = previewBody([item], {
        titleField: 'nadpis',
        bodyField: 'perex',
        imageField: 'obrazek',
      });
      expect(html).toContain('Ahoj');
      expect(html).toContain('Světe');
      expect(html).toContain('src="/o.png"');
    });

    it('prefers the mapping over a conventional field that also exists', () => {
      const both: PreviewItem = { data: { title: 'Guessed', nadpis: 'Chosen' } };
      expect(previewBody([both], { titleField: 'nadpis' })).toContain('Chosen');
      expect(previewBody([both], { titleField: 'nadpis' })).not.toContain('Guessed');
    });

    it('escapes item content', () => {
      expect(previewBody([{ data: { title: '<img onerror=x>' } }], {})).toContain('&lt;img');
    });

    it('lets a slot be emptied explicitly', () => {
      // Empty means "guess", so there had to be a third value meaning "don't".
      const html = previewBody([post()], { bodyField: '-' });
      expect(html).not.toContain('World');
      expect(html).toContain('Hello');
    });
  });
});

describe('previewHtml', () => {
  it('puts the heading above the body', () => {
    const html = previewHtml([post()], { heading: 'Latest posts' });
    expect(html.indexOf('dcms-heading')).toBeLessThan(html.indexOf('dcms-card'));
    expect(html).toContain('Latest posts');
  });

  it('omits the heading when there is none', () => {
    expect(previewHtml([post()], {})).not.toContain('dcms-heading');
  });

  it('puts the “see all” link beside the heading, not under the last card', () => {
    const html = previewHtml([post()], {
      heading: 'Latest posts',
      moreLabel: 'See all',
      moreHref: '/blog',
    });
    expect(html.indexOf('dcms-more')).toBeLessThan(html.indexOf('dcms-card'));
    expect(html).toContain('href="/blog"');
  });

  it('needs both a label and a link before it shows one', () => {
    expect(previewHtml([post()], { moreLabel: 'See all' })).not.toContain('dcms-more');
    expect(previewHtml([post()], { moreHref: '/blog' })).not.toContain('dcms-more');
  });
});

describe('field mapping into tenant-defined fields', () => {
  // The values of tenant-defined fields are nested under one key on the item.
  const item: PreviewItem = {
    data: { title: 'Plugin title', values: { nadpis: 'Tenant title', perex: 'Tenant text' } },
  };

  it('reads a mapped field by its full path', () => {
    expect(readField(fieldsOf(item), 'values.nadpis')).toBe('Tenant title');
  });

  it('finds a nested field by its bare name, which is what the author sees', () => {
    expect(readField(fieldsOf(item), 'nadpis')).toBe('Tenant title');
  });

  it('prefers a top-level field of the same name', () => {
    const both: PreviewItem = { data: { note: 'top', values: { note: 'nested' } } };
    expect(readField(fieldsOf(both), 'note')).toBe('top');
  });

  it('renders a card from a tenant field, which is the whole point', () => {
    const html = previewBody([item], { titleField: 'values.nadpis', bodyField: 'values.perex' });
    expect(html).toContain('Tenant title');
    expect(html).toContain('Tenant text');
  });

  it('resolves to nothing for a path that is not there', () => {
    expect(readField(fieldsOf(item), 'values.missing')).toBeUndefined();
  });
});

describe('previewNotice', () => {
  it('names the missing instance so the fix is obvious', () => {
    expect(previewNotice('missing', 'blog')).toContain('blog');
  });

  it('carries its kind as a class, for styling', () => {
    expect(previewNotice('loading')).toContain('dcms-notice-loading');
  });

  it('escapes the detail', () => {
    expect(previewNotice('missing', '<x>')).toContain('&lt;x&gt;');
  });
});
