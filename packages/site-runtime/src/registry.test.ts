import { describe, expect, it } from 'vitest';
import { propValueSchema, type PropDefinition } from './props';
import { canPlace, createRegistry, type ComponentDefinition } from './registry';

const Noop = () => null;

function def(type: string, rest: Partial<ComponentDefinition> = {}): ComponentDefinition {
  return { type, version: 1, label: type.split('.')[1]!, category: 'test', component: Noop, props: [], ...rest };
}

const registry = createRegistry([
  def('dcms.section', { slots: [{ name: 'default' }] }),
  def('dcms.hero', {
    slots: [
      { name: 'title', allowed: ['dcms.heading'], max: 1 },
      { name: 'actions', label: 'Actions', allowed: ['dcms.button'] },
    ],
  }),
  def('dcms.heading'),
  def('dcms.button'),
  def('dcms.outlet', { allowedParents: ['dcms.section'], draggable: false }),
]);

describe('canPlace', () => {
  it.each([
    ['dcms.section', 'default', 'dcms.heading', 0],
    ['dcms.section', 'default', 'dcms.hero', 5],
    ['dcms.hero', 'title', 'dcms.heading', 0],
    ['dcms.hero', 'actions', 'dcms.button', 12],
    ['dcms.section', 'default', 'dcms.outlet', 0],
  ])('allows %s › %s ← %s', (parent, slot, child, siblings) => {
    expect(canPlace(registry, parent, slot, child, siblings)).toEqual({ ok: true });
  });

  it.each([
    ['dcms.heading', 'default', 'dcms.button', 0, 'heading cannot contain other components.'],
    ['dcms.hero', 'body', 'dcms.heading', 0, 'hero has no slot “body” (it has title, actions).'],
    ['dcms.hero', 'actions', 'dcms.heading', 0, 'hero › Actions accepts only button, not heading.'],
    ['dcms.hero', 'title', 'dcms.heading', 1, 'hero › title holds at most 1.'],
    ['dcms.hero', 'title', 'dcms.outlet', 0, 'hero › title accepts only heading, not outlet.'],
    ['dcms.missing', 'default', 'dcms.heading', 0, '“dcms.missing” is not a known component.'],
    ['dcms.section', 'default', 'dcms.missing', 0, '“dcms.missing” is not a known component.'],
  ])('refuses %s › %s ← %s', (parent, slot, child, siblings, reason) => {
    expect(canPlace(registry, parent, slot, child, siblings)).toEqual({ ok: false, reason });
  });

  it('honours allowedParents even where the slot accepts anything', () => {
    const r = createRegistry([...registry.values(), def('dcms.stack', { slots: [{ name: 'default' }] })]);
    expect(canPlace(r, 'dcms.stack', 'default', 'dcms.outlet')).toEqual({
      ok: false,
      reason: 'outlet can only be placed inside section.',
    });
  });
});

describe('createRegistry', () => {
  it.each<[string, ComponentDefinition[], RegExp]>([
    ['a duplicate type', [def('dcms.a'), def('dcms.a')], /registered twice/],
    ['a type without namespace', [def('heading')], /namespace\.name/],
    ['a slot naming an unregistered type', [def('dcms.a', { slots: [{ name: 'x', allowed: ['dcms.ghost'] }] })], /dcms\.ghost/],
    ['a duplicate slot', [def('dcms.a', { slots: [{ name: 'x' }, { name: 'x' }] })], /declared twice/],
    [
      'a default its own prop rejects',
      [def('dcms.a', { props: [{ kind: 'select', name: 'size', label: 'Size', options: [{ value: 's', label: 'S' }], default: 'xl' }] })],
      /default is not a valid value/,
    ],
    ['a malformed prop', [def('dcms.a', { props: [{ kind: 'text', name: '1bad', label: 'x' } as PropDefinition] })], /prop “1bad”/],
  ])('refuses %s', (_, defs, error) => {
    expect(() => createRegistry(defs)).toThrow(error);
  });
});

describe('propValueSchema', () => {
  const ok = (prop: PropDefinition, value: unknown) => propValueSchema(prop).safeParse(value).success;
  const base = { name: 'p', label: 'P' };

  it('keeps links to safe protocols', () => {
    const url: PropDefinition = { kind: 'url', ...base };
    expect(ok(url, '/contact')).toBe(true);
    expect(ok(url, 'https://example.com')).toBe(true);
    expect(ok(url, 'javascript:alert(1)')).toBe(false);
    expect(ok(url, '//evil.example')).toBe(false);
  });

  it('keeps colours to a token or a hex literal', () => {
    const color: PropDefinition = { kind: 'color', ...base };
    expect(ok(color, 'brand')).toBe(true);
    expect(ok(color, '#0af')).toBe(true);
    expect(ok(color, 'red; background:url(x)')).toBe(false);
  });

  it('enforces number bounds and select options', () => {
    expect(ok({ kind: 'number', ...base, min: 1, max: 4 }, 5)).toBe(false);
    expect(ok({ kind: 'number', ...base }, Number.NaN)).toBe(false);
    expect(ok({ kind: 'select', ...base, options: [{ value: 'a', label: 'A' }] }, 'b')).toBe(false);
  });

  it('accepts dates with or without a time', () => {
    const date: PropDefinition = { kind: 'date', ...base };
    expect(ok(date, '2026-10-02')).toBe(true);
    expect(ok(date, '2026-10-02T12:00:00+02:00')).toBe(true);
    expect(ok(date, 'tomorrow')).toBe(false);
  });
});
