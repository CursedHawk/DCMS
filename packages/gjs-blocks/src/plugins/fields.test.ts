import { describe, expect, it } from 'vitest';
import { META_FIELDS, contentFields, suggestedTarget } from './fields';
import type { PluginContentTypeLike } from './specs';

const post: PluginContentTypeLike = {
  name: 'post',
  slugField: 'slug',
  fields: [
    { name: 'title', type: 'Text' },
    { name: 'body', type: 'RichText' },
    { name: 'coverImage', type: 'MediaRef' },
    { name: 'publishedAt', type: 'DateTime' },
  ],
};

describe('contentFields', () => {
  it('lists the plugin’s own fields with readable labels', () => {
    const fields = contentFields(post);
    expect(fields.map((f) => f.path)).toEqual(['title', 'body', 'coverImage', 'publishedAt']);
    expect(fields.find((f) => f.path === 'coverImage')?.label).toBe('Cover image');
  });

  it('classifies each field well enough to suggest what to do with it', () => {
    const kinds = Object.fromEntries(contentFields(post).map((f) => [f.path, f.kind]));
    expect(kinds).toMatchObject({
      title: 'text',
      body: 'richText',
      coverImage: 'media',
      publishedAt: 'date',
    });
  });

  it('appends the tenant’s own fields at the path their values live at', () => {
    // Binding to the bare key renders nothing, which looks exactly like an empty
    // field — the single easiest way to lose an afternoon in this builder.
    const fields = contentFields({ ...post, customFields: { valuesField: 'values' } }, [
      { key: 'perex', label: 'Perex', type: 'longText' },
    ]);
    const custom = fields.find((f) => f.label === 'Perex')!;
    expect(custom.path).toBe('values.perex');
    expect(custom.custom).toBe(true);
    expect(custom.kind).toBe('richText');
  });

  it('leaves a custom field unprefixed when the type does not nest them', () => {
    const fields = contentFields(post, [{ key: 'subtitle', label: 'Subtitle', type: 'text' }]);
    expect(fields.find((f) => f.label === 'Subtitle')?.path).toBe('subtitle');
  });

  it('falls back to text for a field type it has never seen', () => {
    const fields = contentFields({ name: 'x', fields: [{ name: 'weird', type: 'Quaternion' }] });
    expect(fields[0]!.kind).toBe('text');
  });

  it('labels a tenant field by its key when the config gave it no label', () => {
    const fields = contentFields(post, [{ key: 'openingHours', label: '', type: 'text' }]);
    expect(fields.find((f) => f.path === 'openingHours')?.label).toBe('Opening hours');
  });
});

describe('META_FIELDS', () => {
  it('offers the envelope, which is how a card links to its own page', () => {
    expect(META_FIELDS.map((f) => f.path)).toContain('#slug');
  });
});

describe('suggestedTarget', () => {
  it('puts each kind where it belongs by default', () => {
    expect(suggestedTarget('media')).toBe('src');
    expect(suggestedTarget('richText')).toBe('html');
    expect(suggestedTarget('url')).toBe('href');
    expect(suggestedTarget('text')).toBe('text');
    expect(suggestedTarget('date')).toBe('text');
  });
});
