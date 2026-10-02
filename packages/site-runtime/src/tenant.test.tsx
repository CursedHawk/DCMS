import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { BUILTIN_COMPONENTS } from './components';
import type { Node } from './document';
import { RenderNode } from './render';
import { componentPath, siteRegistry, tenantComponentSchema, type TenantComponentDoc } from './tenant';
import { checkVisualSite } from './validate';

const card = (version: number, headingLevel: string): TenantComponentDoc =>
  tenantComponentSchema.parse({
    schemaVersion: 1,
    name: 'event-card',
    version,
    label: 'Event card',
    props: [
      { kind: 'text', name: 'title', label: 'Title', default: 'Untitled' },
      { kind: 'url', name: 'link', label: 'Link' },
    ],
    slots: [{ name: 'extra', label: 'Extra' }],
    bindings: { title: [{ node: 't', prop: 'text' }], link: [{ node: 'b', action: 'link' }] },
    slotTargets: { extra: { node: 'more', slot: 'default' } },
    root: {
      id: 'box',
      type: 'dcms.stack',
      slots: {
        default: [
          { id: 't', type: 'dcms.heading', props: { level: headingLevel } },
          { id: 'b', type: 'dcms.button', props: { label: 'More' } },
          { id: 'more', type: 'dcms.stack' },
        ],
      },
    },
  });

const { registry } = siteRegistry(BUILTIN_COMPONENTS, [card(1, '3'), card(2, '2')]);
const html = (node: Node) => renderToStaticMarkup(<RenderNode node={node} registry={registry} />);

describe('tenant components', () => {
  it('renders the template with the instance’s settings and its slot content in place', () => {
    const out = html({
      id: 'i',
      type: 'tenant.event-card',
      version: 2,
      props: { title: 'Summer party', link: '/events/summer' },
      slots: { extra: [{ id: 'x', type: 'dcms.text', props: { text: 'Bring a friend' } }] },
    });
    expect(out).toContain('<h2 class="dcms-heading dcms-heading-2 dcms-text-start">Summer party</h2>');
    expect(out).toContain('href="/events/summer"');
    // The instance's child lands in the inner stack's slot, after the template's own children.
    expect(out).toMatch(/dcms-heading.*More.*Bring a friend/s);
  });

  it('keeps an instance on the version it pinned', () => {
    expect(html({ id: 'i', type: 'tenant.event-card', version: 1 })).toContain('<h3 class="dcms-heading dcms-heading-3');
  });

  it('turns a link setting into a link only for safe addresses', () => {
    expect(html({ id: 'i', type: 'tenant.event-card', props: { link: 'javascript:alert(1)' } })).not.toContain('href');
  });

  it('refuses an exposed slot wired to a slot the template already fills', () => {
    const bad = { ...card(1, '2'), slotTargets: { extra: { node: 'box', slot: 'default' } } };
    expect(tenantComponentSchema.safeParse(bad).error?.issues[0]?.message).toBe('the slot “extra” must be wired to an empty slot inside');
  });

  it('refuses a template wired to nodes that are not in it', () => {
    const bad = { ...card(1, '2'), bindings: { title: [{ node: 'nope', prop: 'text' }] } };
    expect(tenantComponentSchema.safeParse(bad).success).toBe(false);
  });
});

describe('checkVisualSite with components', () => {
  const json = (v: unknown) => `${JSON.stringify(v, null, 2)}\n`;
  const base = {
    'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
  };
  const page = (children: Node[]) =>
    json({ schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'r', type: 'dcms.page', slots: { default: children } } });

  it('reports an instance pinned to a version that does not exist', () => {
    const problems = checkVisualSite({
      ...base,
      [componentPath('event-card', 1)]: json(card(1, '2')),
      'dcms/pages/home.json': page([{ id: 'i', type: 'tenant.event-card', version: 7 }]),
    });
    expect(problems.map((p) => p.message)).toContain('“tenant.event-card” has no version 7.');
  });

  it('reports components that contain each other', () => {
    const a = { ...card(1, '2'), name: 'a', slots: [], slotTargets: {}, root: { id: 'r', type: 'dcms.stack', slots: { default: [{ id: 'x', type: 'tenant.b' }] } }, bindings: {}, props: [] };
    const b = { ...a, name: 'b', root: { id: 'r', type: 'dcms.stack', slots: { default: [{ id: 'y', type: 'tenant.a' }] } } };
    const problems = checkVisualSite({
      ...base,
      'dcms/pages/home.json': page([]),
      [componentPath('a', 1)]: json(a),
      [componentPath('b', 1)]: json(b),
    });
    expect(problems.some((p) => p.message.startsWith('Components contain each other in a loop'))).toBe(true);
  });

  it('refuses a file whose content names another component or version', () => {
    const problems = checkVisualSite({ ...base, 'dcms/pages/home.json': page([]), [componentPath('event-card', 3)]: json(card(1, '2')) });
    expect(problems[0]?.message).toBe('says it is event-card v1, but the file is event-card v3.');
  });
});
