import { defaultProps, defaultQuery, identityClassOf } from '@dcms/gjs-schema';
import { describe, expect, it } from 'vitest';
import {
  detailType,
  listType,
  pluginSpecs,
  type PluginInstanceLike,
  type PluginManifestLike,
} from './specs';

const blogManifest: PluginManifestLike = {
  id: 'blog',
  name: 'Blog',
  contentTypes: [
    {
      name: 'post',
      slugField: 'slug',
      fields: [
        { name: 'title', type: 'Text' },
        { name: 'excerpt', type: 'Text' },
        { name: 'body', type: 'RichText' },
        { name: 'coverImage', type: 'MediaRef' },
        { name: 'publishedAt', type: 'DateTime' },
      ],
    },
    {
      name: 'author',
      fields: [
        { name: 'name', type: 'Text' },
        { name: 'bio', type: 'Markdown' },
      ],
    },
  ],
};

const galleryManifest: PluginManifestLike = {
  id: 'gallery',
  name: 'Gallery',
  contentTypes: [
    {
      name: 'photo',
      fields: [
        { name: 'caption', type: 'Text' },
        { name: 'image', type: 'MediaRef' },
      ],
    },
  ],
};

const instance = (over: Partial<PluginInstanceLike> = {}): PluginInstanceLike => ({
  id: 'i1',
  pluginId: 'blog',
  slug: 'blog',
  name: 'Company blog',
  enabled: true,
  ...over,
});

const types = (specs: { type: string }[]) => specs.map((s) => s.type);

describe('pluginSpecs', () => {
  it('returns nothing without instances', () => {
    expect(pluginSpecs([blogManifest], [])).toEqual([]);
  });

  it('skips disabled instances', () => {
    expect(pluginSpecs([blogManifest], [instance({ enabled: false })])).toEqual([]);
  });

  it('skips an instance whose plugin is not in the catalogue', () => {
    expect(pluginSpecs([galleryManifest], [instance()])).toEqual([]);
  });

  it('generates a list per content type', () => {
    const specs = pluginSpecs([blogManifest], [instance()]);
    expect(types(specs)).toContain(listType('blog', 'post'));
    expect(types(specs)).toContain(listType('blog', 'author'));
  });

  it('generates a detail only where the content type has a slug field', () => {
    const specs = pluginSpecs([blogManifest], [instance()]);
    expect(types(specs)).toContain(detailType('blog', 'post'));
    expect(types(specs)).not.toContain(detailType('blog', 'author'));
  });

  it('keeps two instances of one plugin apart', () => {
    const specs = pluginSpecs(
      [blogManifest],
      [instance(), instance({ id: 'i2', slug: 'news', name: 'Newsroom' })],
    );
    expect(types(specs)).toContain(listType('blog', 'post'));
    expect(types(specs)).toContain(listType('news', 'post'));
    // Distinct types mean distinct identity classes, so the canvas can tell a
    // Newsroom block from a Blog block when reading markup back.
    const identities = new Set(specs.map(identityClassOf));
    expect(identities.size).toBe(specs.length);
  });

  it('groups blocks by the instance name, not the plugin name', () => {
    const specs = pluginSpecs([blogManifest], [instance({ name: 'Newsroom' })]);
    expect(specs.every((s) => s.group === 'Newsroom')).toBe(true);
  });

  it('marks every generated spec as a plugin component gated on its plugin', () => {
    const specs = pluginSpecs([blogManifest], [instance()]);
    expect(specs.every((s) => s.category === 'plugin')).toBe(true);
    expect(specs.every((s) => s.requiredPluginId === 'blog')).toBe(true);
    expect(specs.every((s) => s.tag === 'div' && !s.acceptsChildren)).toBe(true);
  });

  describe('bindings', () => {
    const specs = pluginSpecs([blogManifest], [instance()]);
    const list = specs.find((s) => s.type === listType('blog', 'post'))!;
    const detail = specs.find((s) => s.type === detailType('blog', 'post'))!;

    it('binds a list to the instance and content type', () => {
      expect(list.binding).toEqual({
        propPath: 'items',
        contentType: 'post',
        instanceSlug: 'blog',
      });
    });

    it('binds a detail to a single item', () => {
      expect(detail.binding?.propPath).toBe('item');
    });

    it('puts the content type in the default query, which hydrate.js needs', () => {
      expect(defaultQuery(list)).toMatchObject({ contentType: 'post', pageSize: 10 });
    });
  });

  describe('traits', () => {
    const specs = pluginSpecs([blogManifest], [instance()]);
    const list = specs.find((s) => s.type === listType('blog', 'post'))!;
    const detail = specs.find((s) => s.type === detailType('blog', 'post'))!;
    const named = (name: string) => list.traits.find((t) => t.name === name);

    it('offers presentation settings as props', () => {
      for (const name of ['heading', 'layout', 'emptyText']) {
        expect(named(name)?.target).toBe('prop');
      }
    });

    it('offers paging as query settings', () => {
      expect(named('pageSize')?.target).toBe('query');
      expect(named('page')?.target).toBe('query');
    });

    it('lets a detail view be pinned to one item', () => {
      const itemSlug = detail.traits.find((t) => t.name === 'itemSlug');
      expect(itemSlug?.target).toBe('query');
      expect(list.traits.find((t) => t.name === 'itemSlug')).toBeUndefined();
    });

    it('offers only textual fields as the title source', () => {
      const options = named('titleField')?.options?.map((o) => o.value);
      expect(options).toEqual(['', '-', 'title', 'excerpt', 'body']);
    });

    it('offers only media fields as the image source', () => {
      expect(named('imageField')?.options?.map((o) => o.value)).toEqual(['', '-', 'coverImage']);
    });

    it('lets a slot be emptied as well as guessed or mapped', () => {
      // Without a "None", an author who did not want an excerpt had no way to
      // say so — the guess would find `excerpt` whatever they did.
      for (const name of ['titleField', 'bodyField', 'imageField', 'metaField', 'tagsField', 'linkField']) {
        const options = named(name)?.options?.map((o) => o.value);
        expect(options, name).toContain('-');
      }
    });

    it('offers the collection controls on a list and not on a detail', () => {
      // A single item has no arrangement or column count; offering them would be
      // settings that silently do nothing.
      for (const name of ['arrangement', 'columns', 'cardVariant']) {
        expect(named(name)?.target, name).toBe('prop');
        expect(detail.traits.find((t) => t.name === name), name).toBeUndefined();
      }
    });

    it('includes tenant-defined custom fields', () => {
      const withCustom = pluginSpecs([blogManifest], [instance()], {
        customFields: (slug, contentType) =>
          slug === 'blog' && contentType === 'post'
            ? [{ key: 'subtitle', label: 'Subtitle', type: 'text' }]
            : [],
      });
      const spec = withCustom.find((s) => s.type === listType('blog', 'post'))!;
      const options = spec.traits.find((t) => t.name === 'titleField')?.options?.map((o) => o.value);
      expect(options).toContain('subtitle');
    });

    it('offers a custom field at the path its value actually lives at', () => {
      // The values of tenant-defined fields are nested under one key, so a bare
      // key resolves to nothing on the page. Every option in this dropdown was a
      // field that rendered blank once chosen.
      const nested = pluginSpecs(
        [
          {
            ...blogManifest,
            contentTypes: blogManifest.contentTypes.map((c) =>
              c.name === 'post' ? { ...c, customFields: { valuesField: 'values' } } : c,
            ),
          },
        ],
        [instance()],
        { customFields: () => [{ key: 'subtitle', label: 'Subtitle', type: 'text' }] },
      );
      const spec = nested.find((s) => s.type === listType('blog', 'post'))!;
      const options = spec.traits.find((t) => t.name === 'titleField')?.options?.map((o) => o.value);
      expect(options).toContain('values.subtitle');
      expect(options).not.toContain('subtitle');
    });

    it('defaults a detail view to the article layout', () => {
      expect(defaultProps(detail).layout).toBe('article');
    });

    it('defaults a list of a media-bearing type to cards', () => {
      expect(defaultProps(list).layout).toBe('cards');
    });

    it('defaults a list of a text-only type to a plain list', () => {
      const author = specs.find((s) => s.type === listType('blog', 'author'))!;
      expect(defaultProps(author).layout).toBe('list');
    });

    it('guesses a media layout from the content type name', () => {
      const media: PluginManifestLike = {
        id: 'media',
        name: 'Media',
        contentTypes: [
          { name: 'video', fields: [{ name: 'source', type: 'MediaRef' }] },
          { name: 'track', fields: [{ name: 'source', type: 'MediaRef' }] },
          { name: 'download', fields: [{ name: 'file', type: 'MediaRef' }] },
        ],
      };
      const found = pluginSpecs(
        [media],
        [instance({ pluginId: 'media', slug: 'media', name: 'Media library' })],
      );
      const layoutOf = (contentType: string) =>
        defaultProps(found.find((s) => s.type === listType('media', contentType))!).layout;
      expect(layoutOf('video')).toBe('video');
      expect(layoutOf('track')).toBe('audio');
      expect(layoutOf('download')).toBe('downloads');
    });
  });

  it('labels components in words rather than field ids', () => {
    const manifest: PluginManifestLike = {
      id: 'shop',
      name: 'Shop',
      contentTypes: [{ name: 'productVariant', fields: [{ name: 'sku', type: 'Text' }] }],
    };
    const specs = pluginSpecs(
      [manifest],
      [instance({ pluginId: 'shop', slug: 'shop', name: 'Shop' })],
    );
    expect(specs[0]!.label).toBe('Product variant list');
  });
});
