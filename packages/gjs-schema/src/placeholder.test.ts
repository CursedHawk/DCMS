import { describe, expect, it } from 'vitest';
import {
  BINDINGS_ATTR,
  COMPONENT_ATTR,
  PROPS_ATTR,
  decodePlaceholder,
  encodePlaceholder,
  isPlaceholder,
  placeholderErrors,
} from './placeholder';

describe('encodePlaceholder', () => {
  it('emits the flat binding shape hydrate.js reads', () => {
    const attrs = encodePlaceholder({
      component: 'BlogList',
      props: { heading: 'Latest posts' },
      bindings: [{ propPath: 'items', instanceSlug: 'blog', query: { contentType: 'post', pageSize: 10 } }],
    });

    expect(attrs[COMPONENT_ATTR]).toBe('BlogList');
    expect(JSON.parse(attrs[PROPS_ATTR])).toEqual({ heading: 'Latest posts' });
    expect(JSON.parse(attrs[BINDINGS_ATTR])).toEqual([
      { propPath: 'items', instanceSlug: 'blog', query: { contentType: 'post', pageSize: 10 } },
    ]);
  });

  it('omits empty props and bindings so an unbound component diffs cleanly', () => {
    const attrs = encodePlaceholder({ component: 'SearchBox', props: {}, bindings: [] });
    expect(attrs).toEqual({ [COMPONENT_ATTR]: 'SearchBox' });
  });
});

describe('decodePlaceholder', () => {
  it('round-trips an encoded placeholder', () => {
    const data = {
      component: 'GalleryGrid',
      props: { heading: 'Photos', columns: 3 },
      bindings: [{ propPath: 'items', instanceSlug: 'gallery', query: { contentType: 'gallery' } }],
    };
    expect(decodePlaceholder(encodePlaceholder(data))).toEqual(data);
  });

  it('accepts the legacy nested binding shape', () => {
    const decoded = decodePlaceholder({
      [COMPONENT_ATTR]: 'BlogList',
      [BINDINGS_ATTR]: JSON.stringify([
        { propPath: 'items', source: { instanceSlug: 'blog', query: { contentType: 'post' } } },
      ]),
    });
    expect(decoded?.bindings).toEqual([
      { propPath: 'items', instanceSlug: 'blog', query: { contentType: 'post' } },
    ]);
  });

  it('returns null for a non-placeholder element', () => {
    expect(decodePlaceholder({ class: 'hero' })).toBeNull();
    expect(decodePlaceholder({ [COMPONENT_ATTR]: '' })).toBeNull();
  });

  it('degrades to empty on malformed JSON instead of throwing', () => {
    const decoded = decodePlaceholder({
      [COMPONENT_ATTR]: 'BlogList',
      [PROPS_ATTR]: '{not json',
      [BINDINGS_ATTR]: '[[[',
    });
    expect(decoded).toEqual({ component: 'BlogList', props: {}, bindings: [] });
  });

  it('drops a binding with no instance slug', () => {
    const decoded = decodePlaceholder({
      [COMPONENT_ATTR]: 'BlogList',
      [BINDINGS_ATTR]: JSON.stringify([{ propPath: 'items', query: { contentType: 'post' } }]),
    });
    expect(decoded?.bindings).toEqual([]);
  });
});

describe('placeholderErrors', () => {
  it('reports malformed JSON that decodePlaceholder silently tolerates', () => {
    const errors = placeholderErrors({ [COMPONENT_ATTR]: 'BlogList', [PROPS_ATTR]: '{not json' });
    expect(errors).toHaveLength(1);
    expect(errors[0].attribute).toBe(PROPS_ATTR);
  });

  it('reports a bindings attribute that is not an array', () => {
    const errors = placeholderErrors({ [COMPONENT_ATTR]: 'X', [BINDINGS_ATTR]: '{"a":1}' });
    expect(errors[0].message).toContain('must be a JSON array');
  });

  it('is silent on a valid placeholder', () => {
    expect(placeholderErrors(encodePlaceholder({ component: 'X', props: { a: 1 }, bindings: [] }))).toEqual([]);
  });
});

describe('isPlaceholder', () => {
  it('detects the marker attribute', () => {
    expect(isPlaceholder({ [COMPONENT_ATTR]: 'BlogList' })).toBe(true);
    expect(isPlaceholder({ class: 'hero' })).toBe(false);
  });
});
