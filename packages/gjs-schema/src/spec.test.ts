import { describe, expect, it } from 'vitest';
import type { DcmsComponentSpec } from './spec';
import {
  availableSpecs,
  classesOf,
  defaultAttributes,
  defaultProps,
  findSpec,
  groupSpecs,
  identityClassOf,
} from './spec';

const hero: DcmsComponentSpec = {
  type: 'Hero',
  label: 'Hero',
  category: 'section',
  tag: 'section',
  acceptsChildren: true,
  order: 1,
  traits: [
    { name: 'data-align', label: 'Align', kind: 'select', default: 'center' },
    { name: 'data-full-height', label: 'Full height', kind: 'checkbox' },
  ],
};

const blogList: DcmsComponentSpec = {
  type: 'BlogList',
  label: 'Blog list',
  category: 'plugin',
  tag: 'div',
  group: 'Blog',
  acceptsChildren: false,
  requiredPluginId: 'blog',
  binding: { propPath: 'items', contentType: 'post' },
  traits: [
    { name: 'heading', label: 'Heading', kind: 'text', target: 'prop', default: 'Latest posts' },
    { name: 'pageSize', label: 'Items', kind: 'number', target: 'prop', default: 10 },
  ],
};

const chat: DcmsComponentSpec = {
  type: 'ChatWidget',
  label: 'Live chat',
  category: 'plugin',
  tag: 'div',
  acceptsChildren: false,
  requiredPluginId: 'live-chat',
  traits: [],
};

describe('availableSpecs', () => {
  it('keeps specs with no plugin requirement', () => {
    expect(availableSpecs([hero], new Set())).toEqual([hero]);
  });

  it('filters plugin specs down to enabled plugins', () => {
    expect(availableSpecs([hero, blogList, chat], new Set(['blog']))).toEqual([hero, blogList]);
  });
});

describe('findSpec', () => {
  it('looks a spec up by type', () => {
    expect(findSpec([hero, blogList], 'BlogList')).toBe(blogList);
    expect(findSpec([hero], 'Nope')).toBeUndefined();
  });
});

describe('groupSpecs', () => {
  it('splits by category then by group', () => {
    const grouped = groupSpecs([hero, blogList, chat]);
    const plugin = grouped.find((g) => g.category === 'plugin');
    expect(plugin?.groups.map((g) => g.group)).toEqual(['Blog', null]);
    expect(grouped.find((g) => g.category === 'section')?.groups[0].specs).toEqual([hero]);
  });

  it('orders within a group by order then label', () => {
    const later: DcmsComponentSpec = { ...hero, type: 'Aside', label: 'Aside', order: 0 };
    const grouped = groupSpecs([hero, later]);
    expect(grouped[0].groups[0].specs.map((s) => s.type)).toEqual(['Aside', 'Hero']);
  });
});

describe('identityClassOf', () => {
  it('derives a kebab-case class from the type', () => {
    expect(identityClassOf(hero)).toBe('dcms-hero');
    expect(identityClassOf(blogList)).toBe('dcms-blog-list');
  });

  it('honours an explicit override', () => {
    expect(identityClassOf({ ...hero, identityClass: 'my-hero' })).toBe('my-hero');
  });

  it('puts the identity class first, then the extras', () => {
    expect(classesOf({ ...hero, classes: ['dcms-section'] })).toEqual(['dcms-hero', 'dcms-section']);
  });
});

describe('defaults', () => {
  it('collects attribute defaults, skipping prop traits', () => {
    expect(defaultAttributes(hero)).toEqual({ 'data-align': 'center' });
    expect(defaultAttributes(blogList)).toEqual({});
  });

  it('collects prop defaults, skipping attribute traits', () => {
    expect(defaultProps(blogList)).toEqual({ heading: 'Latest posts', pageSize: 10 });
    expect(defaultProps(hero)).toEqual({});
  });
});
