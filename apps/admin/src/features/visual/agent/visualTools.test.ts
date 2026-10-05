import { beforeEach, describe, expect, it } from 'vitest';
import { createTransaction } from '../../agent/transaction';
import { vfsPort } from '../../agent/vfsPort';
import { useVfs } from '../../site-source/vfs';
import { useVisual } from '../store';
import { VISUAL_TOOLS, validateVisualSite } from './visualTools';

/**
 * The Mode D agent's tools against a real working draft and a real run transaction: what they
 * write is what lands in the files, and what they refuse is refused for the reason the canvas
 * would give.
 */

const json = (v: unknown) => `${JSON.stringify(v, null, 2)}\n`;

function seed() {
  useVfs.getState().load(
    {
      'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
      'dcms/pages/home.json': json({ schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'root', type: 'dcms.page', slots: { default: [] } } }),
      'dcms/theme.json': json({}),
    },
    1,
    {},
  );
}

function tool(name: string) {
  const t = VISUAL_TOOLS.find((x) => x.name === name);
  if (!t) throw new Error(`no tool ${name}`);
  return t;
}

let ctx: never;
beforeEach(() => {
  seed();
  ctx = { tx: createTransaction(vfsPort()) } as never;
});

const run = (name: string, input: Record<string, unknown>) => tool(name).run(input, ctx);
const page = (id: string) => JSON.parse(useVfs.getState().files[`dcms/pages/${id}.json`]!);

describe('Mode D agent tools', () => {
  it('inserts a whole section in one call, with generated ids, and the site stays valid', async () => {
    const out = await run('insert_node', {
      doc: 'page:home',
      parent: 'root',
      slot: 'default',
      node: {
        type: 'dcms.section',
        props: { background: 'soft' },
        slots: { default: [{ type: 'dcms.heading', props: { text: 'Hi', level: '1' } }, { type: 'dcms.button', props: { label: 'Go' } }] },
      },
    });
    expect(out.isError).toBeFalsy();
    const section = page('home').root.slots.default[0];
    expect(section.type).toBe('dcms.section');
    expect(section.slots.default.map((n: { id: string }) => n.id)).toEqual([expect.stringMatching(/^n[a-z0-9]{8}$/), expect.stringMatching(/^n[a-z0-9]{8}$/)]);
    expect((await validateVisualSite()).ok).toBe(true);
  });

  it('refuses what the canvas would refuse, with the same reason', async () => {
    const out = await run('insert_node', { doc: 'page:home', parent: 'root', slot: 'default', node: { type: 'dcms.page' } });
    expect(out).toMatchObject({ isError: true, content: 'Page cannot be placed inside another component.' });
    const bad = await run('insert_node', { doc: 'page:home', parent: 'root', slot: 'default', node: { type: 'dcms.carousel' } });
    expect(bad.isError).toBe(true);
  });

  it('refuses a prop value the component does not allow, and an override for a prop that cannot differ', async () => {
    await run('insert_node', { doc: 'page:home', parent: 'root', slot: 'default', node: { id: 's', type: 'dcms.stack' } });
    expect((await run('set_props', { doc: 'page:home', node: 's', props: { gap: 'huge' } })).content).toBe('Stack › Space between: "huge" is not an allowed value.');
    expect((await run('set_props', { doc: 'page:home', node: 's', props: { wrap: true }, device: 'mobile' })).content).toBe('Stack › wrap cannot differ on mobile.');
    expect((await run('set_props', { doc: 'page:home', node: 's', props: { direction: 'horizontal' }, device: 'mobile' })).isError).toBeFalsy();
    expect(page('home').root.slots.default[0].responsive).toEqual({ mobile: { direction: 'horizontal' } });
  });

  it('creates a detail page with its route and binds a heading on it', async () => {
    const out = await run('create_page', { title: 'Event', path: '/events/:slug', detail_of: { instance: 'events', contentType: 'event' } });
    expect(out.isError).toBeFalsy();
    const app = JSON.parse(useVfs.getState().files['dcms/app.json']!);
    expect(app.routes).toContainEqual({ id: 'event', path: '/events/:slug', page: 'event' });
    expect(page('event').seo).toEqual({ title: '{title}' });
    const root = page('event').root.id;
    await run('insert_node', { doc: 'page:event', parent: root, slot: 'default', node: { id: 'h', type: 'dcms.heading' } });
    await run('bind_props', { doc: 'page:event', node: 'h', bind: { text: 'title' } });
    expect(page('event').root.slots.default[0].bind).toEqual({ text: 'title' });
    expect((await validateVisualSite()).ok).toBe(true);
  });

  it('builds a component, exposes a setting, and will not edit a version pages use', async () => {
    await run('create_component', { label: 'Event card', root: { id: 'card', type: 'dcms.stack', slots: { default: [{ id: 't', type: 'dcms.heading', props: { text: 'Title' } }] } } });
    expect(JSON.parse(useVfs.getState().files['dcms/components/event-card/v1.json']!).root.slots.default[0].id).toBe('t');
    expect((await run('set_props', { doc: 'component:event-card@1', node: 'card', props: { direction: 'horizontal' } })).content).toBe('Updated card.');
    const exposed = await run('expose_setting', { component: 'component:event-card@1', node: 't', prop: 'text', name: 'title' });
    expect(exposed.content).toBe('Exposed as the setting “title”.');
    await run('insert_node', { doc: 'page:home', parent: 'root', slot: 'default', node: { id: 'card', type: 'tenant.event-card', props: { title: 'Gala' } } });
    expect(page('home').root.slots.default[0]).toMatchObject({ type: 'tenant.event-card', version: 1, props: { title: 'Gala' } });

    const refused = await run('set_props', { doc: 'component:event-card@1', node: 't', props: { level: '3' } });
    expect(refused.content).toMatch(/is used by pages, so it is not edited in place/);
    await run('start_component_version', { name: 'event-card' });
    expect((await run('set_props', { doc: 'component:event-card@2', node: 't', props: { level: '3' } })).content).toBe('Updated t.');
    const updated = await run('update_instances', { name: 'event-card' });
    expect(updated.content).toBe('Updated 1 use(s).');
    expect(page('home').root.slots.default[0].version).toBe(2);
  });

  it('gives an instance its own CSS, and refuses CSS that could leave it', async () => {
    await run('insert_node', { doc: 'page:home', node: { id: 'h', type: 'dcms.heading' } });
    expect((await run('set_css', { doc: 'page:home', node: 'h', css: 'letter-spacing: .1em' })).content).toBe('Updated the CSS of h.');
    expect(page('home').root.slots.default[0].css).toBe('letter-spacing: .1em');
    expect((await run('set_css', { doc: 'page:home', node: 'h', css: 'color: red } body { display: none' })).isError).toBe(true);
    await run('set_css', { doc: 'page:home', node: 'h', css: '' });
    expect(page('home').root.slots.default[0].css).toBeUndefined();
  });

  it('builds tabs from page state: declared values, actions that set them, nodes shown when', async () => {
    await run('update_page', { page: 'home', state: { tab: 'dates' } });
    await run('insert_node', { doc: 'page:home', node: { id: 'b', type: 'dcms.button', props: { label: 'Venues' } } });
    await run('insert_node', { doc: 'page:home', node: { id: 'p', type: 'dcms.text', props: { text: 'Venues panel' } } });
    expect((await run('set_action', { doc: 'page:home', node: 'b', action: { type: 'set-state', key: 'tab', value: 'venues' } })).isError).toBeFalsy();
    expect((await run('set_when', { doc: 'page:home', node: 'p', when: { state: 'tab', equals: 'venues' } })).isError).toBeFalsy();
    expect((await run('set_when', { doc: 'page:home', node: 'p', when: { state: 'nope' } })).content).toBe('This page declares no state “nope”.');
    expect(page('home').state).toEqual({ tab: 'dates' });
    expect(page('home').root.slots.default[1].when).toEqual({ state: 'tab', equals: 'venues' });
    expect((await validateVisualSite()).ok).toBe(true);
  });

  it('inspect_selected names the node the author means by “this”, and where it sits', async () => {
    expect((await run('inspect_selected', {})).content).toBe('Nothing is selected on the canvas.');
    await run('insert_node', { doc: 'page:home', node: { id: 's', type: 'dcms.section', slots: { default: [{ id: 'h', type: 'dcms.heading', props: { text: 'Hi' } }] } } });
    useVisual.setState({ target: { kind: 'page', id: 'home' }, selectedNodeId: () => 'h' });
    const out = JSON.parse((await run('inspect_selected', {})).content as string);
    expect(out).toEqual({
      doc: 'page:home',
      inside: ['dcms.page#root.default', 'dcms.section#s.default'],
      node: { id: 'h', type: 'dcms.heading', props: { text: 'Hi' } },
    });
    useVisual.setState({ target: null, selectedNodeId: () => null });
  });

  it('asks before deleting a page, and never deletes the home page', async () => {
    expect(tool('delete_page').risk).toBe('dangerous');
    expect((await run('delete_page', { page: 'home' })).content).toBe('The home page cannot be deleted.');
  });
});
