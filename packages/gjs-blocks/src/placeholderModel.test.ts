import type { DcmsComponentSpec } from '@dcms/gjs-schema';
import { describe, expect, it } from 'vitest';
import {
  bindPlaceholder,
  readPlaceholder,
  writePlaceholder,
  type PlaceholderHost,
} from './placeholderModel';

/** A stand-in for a GrapesJS component model, with just the surface we use. */
class FakeHost implements PlaceholderHost {
  props = new Map<string, unknown>();
  handlers: { events: string[]; handler: () => void }[] = [];

  constructor(public attributes: Record<string, string> = {}) {}

  get(key: string): unknown {
    return this.props.get(key);
  }

  set(key: string, value: unknown, options?: { silent?: boolean }): unknown {
    this.props.set(key, value);
    if (!options?.silent) this.fire(`change:${key}`);
    return this;
  }

  getAttributes(): Record<string, string> {
    return { ...this.attributes };
  }

  addAttributes(attributes: Record<string, string>): unknown {
    Object.assign(this.attributes, attributes);
    return this;
  }

  removeAttributes(names: string | string[]): unknown {
    for (const name of Array.isArray(names) ? names : [names]) delete this.attributes[name];
    return this;
  }

  on(events: string, handler: () => void): unknown {
    this.handlers.push({ events: events.split(' '), handler });
    return this;
  }

  private fire(event: string): void {
    for (const entry of this.handlers) if (entry.events.includes(event)) entry.handler();
  }

  props_(): Record<string, unknown> {
    return JSON.parse(this.attributes['data-dcms-props'] ?? '{}');
  }

  bindings(): { propPath: string; instanceSlug: string; query: Record<string, unknown> }[] {
    return JSON.parse(this.attributes['data-dcms-bindings'] ?? '[]');
  }
}

const spec: DcmsComponentSpec = {
  type: 'plugin:blog:post:list',
  label: 'Post list',
  category: 'plugin',
  tag: 'div',
  acceptsChildren: false,
  binding: { propPath: 'items', contentType: 'post', instanceSlug: 'blog' },
  traits: [
    { name: 'heading', label: 'Heading', kind: 'text', target: 'prop' },
    { name: 'layout', label: 'Layout', kind: 'select', target: 'prop', default: 'cards' },
    { name: 'pageSize', label: 'How many', kind: 'number', target: 'query', default: 10 },
    { name: 'featured', label: 'Featured', kind: 'checkbox', target: 'query' },
    { name: 'tags', label: 'Tags', kind: 'tags', target: 'query' },
    { name: 'id', label: 'Id', kind: 'text' },
  ],
};

const placeholder = (props?: unknown, bindings?: unknown) => {
  const attrs: Record<string, string> = { 'data-dcms-component': spec.type };
  if (props !== undefined) attrs['data-dcms-props'] = JSON.stringify(props);
  if (bindings !== undefined) attrs['data-dcms-bindings'] = JSON.stringify(bindings);
  return attrs;
};

describe('readPlaceholder', () => {
  it('seeds trait properties from the markup', () => {
    const host = new FakeHost(
      placeholder({ heading: 'Latest' }, [
        { propPath: 'items', instanceSlug: 'blog', query: { contentType: 'post', pageSize: 3 } },
      ]),
    );
    readPlaceholder(host, spec);
    expect(host.get('heading')).toBe('Latest');
    expect(host.get('pageSize')).toBe(3);
  });

  it('falls back to the spec defaults for what the markup omits', () => {
    const host = new FakeHost(placeholder());
    readPlaceholder(host, spec);
    expect(host.get('layout')).toBe('cards');
    expect(host.get('pageSize')).toBe(10);
  });

  it('does not fire change events while seeding', () => {
    const host = new FakeHost(placeholder({ heading: 'Latest' }));
    let fired = 0;
    host.on('change:heading', () => fired++);
    readPlaceholder(host, spec);
    expect(fired).toBe(0);
  });

  it('ignores attribute-targeted traits', () => {
    const host = new FakeHost({ ...placeholder(), id: 'x' });
    readPlaceholder(host, spec);
    expect(host.get('id')).toBeUndefined();
  });
});

describe('writePlaceholder', () => {
  it('writes props into data-dcms-props', () => {
    const host = new FakeHost(placeholder());
    host.set('heading', 'Latest', { silent: true });
    writePlaceholder(host, spec);
    expect(host.props_()).toEqual({ heading: 'Latest' });
  });

  it('writes query traits into the binding, not the props', () => {
    const host = new FakeHost(placeholder());
    host.set('pageSize', 5, { silent: true });
    writePlaceholder(host, spec);
    expect(host.props_().pageSize).toBeUndefined();
    expect(host.bindings()[0]!.query).toMatchObject({ contentType: 'post', pageSize: 5 });
  });

  it('creates the binding from the spec when the markup had none', () => {
    const host = new FakeHost({ 'data-dcms-component': spec.type });
    writePlaceholder(host, spec);
    expect(host.bindings()).toEqual([
      { propPath: 'items', instanceSlug: 'blog', query: { pageSize: 10, contentType: 'post' } },
    ]);
  });

  it('keeps a binding the author repointed at another instance', () => {
    const host = new FakeHost(
      placeholder(undefined, [
        { propPath: 'items', instanceSlug: 'news', query: { contentType: 'post' } },
      ]),
    );
    host.set('pageSize', 4, { silent: true });
    writePlaceholder(host, spec);
    expect(host.bindings()[0]!.instanceSlug).toBe('news');
    expect(host.bindings()[0]!.query.pageSize).toBe(4);
  });

  it('removes a prop that was cleared rather than writing an empty string', () => {
    const host = new FakeHost(placeholder({ heading: 'Latest' }));
    host.set('heading', '', { silent: true });
    writePlaceholder(host, spec);
    expect(host.props_().heading).toBeUndefined();
  });

  it('drops the props attribute entirely when nothing is left', () => {
    const host = new FakeHost(placeholder({ heading: 'Latest' }));
    host.set('heading', '', { silent: true });
    writePlaceholder(host, spec);
    // A stale `{}` attribute would show up as a diff on a component nobody
    // configured.
    expect(host.attributes['data-dcms-props']).toBeUndefined();
  });

  it('coerces a numeric trait out of its string input value', () => {
    const host = new FakeHost(placeholder());
    host.set('pageSize', '7', { silent: true });
    writePlaceholder(host, spec);
    expect(host.bindings()[0]!.query.pageSize).toBe(7);
  });

  it('ignores a number that will not parse', () => {
    const host = new FakeHost(placeholder());
    host.set('pageSize', 'lots', { silent: true });
    writePlaceholder(host, spec);
    expect(host.bindings()[0]!.query.pageSize).toBeUndefined();
  });

  it('coerces a checkbox to a boolean', () => {
    const host = new FakeHost(placeholder());
    host.set('featured', 'true', { silent: true });
    writePlaceholder(host, spec);
    expect(host.bindings()[0]!.query.featured).toBe(true);
  });

  it('splits a tags trait into an array', () => {
    const host = new FakeHost(placeholder());
    host.set('tags', 'news, sport ,', { silent: true });
    writePlaceholder(host, spec);
    expect(host.bindings()[0]!.query.tags).toEqual(['news', 'sport']);
  });

  it('leaves props the author wrote by hand alone', () => {
    const host = new FakeHost(placeholder({ custom: 'kept' }));
    host.set('heading', 'Latest', { silent: true });
    writePlaceholder(host, spec);
    expect(host.props_()).toEqual({ custom: 'kept', heading: 'Latest' });
  });

  it('always keeps the component attribute', () => {
    const host = new FakeHost(placeholder());
    writePlaceholder(host, spec);
    expect(host.attributes['data-dcms-component']).toBe(spec.type);
  });
});

describe('bindPlaceholder', () => {
  it('writes back whenever a watched trait changes', () => {
    const host = new FakeHost(placeholder());
    bindPlaceholder(host, spec);
    host.set('heading', 'Latest');
    expect(host.props_().heading).toBe('Latest');
  });

  it('watches query traits too', () => {
    const host = new FakeHost(placeholder());
    bindPlaceholder(host, spec);
    host.set('pageSize', 2);
    expect(host.bindings()[0]!.query.pageSize).toBe(2);
  });

  it('does not write back for an attribute trait', () => {
    const host = new FakeHost(placeholder());
    bindPlaceholder(host, spec);
    host.set('id', 'x');
    expect(host.attributes['data-dcms-props']).toBeUndefined();
  });

  it('registers nothing for a spec with no prop or query traits', () => {
    const bare: DcmsComponentSpec = { ...spec, traits: [] };
    const host = new FakeHost(placeholder());
    expect(bindPlaceholder(host, bare)).toEqual([]);
    expect(host.handlers).toHaveLength(0);
  });

  it('round-trips: read what was written, unchanged', () => {
    const host = new FakeHost(placeholder());
    bindPlaceholder(host, spec);
    host.set('heading', 'Latest');
    host.set('pageSize', 4);

    const reopened = new FakeHost(host.getAttributes());
    readPlaceholder(reopened, spec);
    expect(reopened.get('heading')).toBe('Latest');
    expect(reopened.get('pageSize')).toBe(4);
  });
});
