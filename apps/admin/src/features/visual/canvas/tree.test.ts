import { builtinRegistry, type Node } from '@dcms/site-runtime';
import grapesjs, { type Component, type Editor } from 'grapesjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ID, PROPS, SLOT, fromGrapes, toGrapes } from './tree';
import { registerVisualTypes } from './types';

/**
 * The page tree through GrapesJS and back.
 *
 * This is the Mode D equivalent of the Mode A round-trip suite and just as load-bearing: the
 * canvas captures on every edit, so a node that does not survive load → capture unchanged is
 * a page that gets quietly rewritten — or emptied — the first time someone touches it.
 */

let editor: Editor;

beforeEach(() => {
  const container = document.createElement('div');
  document.body.appendChild(container);
  editor = grapesjs.init({
    container,
    headless: true,
    storageManager: false,
    panels: { defaults: [] },
    plugins: [(e) => registerVisualTypes(e, builtinRegistry)],
  });
});

afterEach(() => editor.destroy());

function load(root: Node): Component {
  editor.setComponents(toGrapes(root, builtinRegistry) as never);
  return editor.getWrapper()!.components().at(0);
}

const page: Node = {
  id: 'root',
  type: 'dcms.page',
  slots: {
    default: [
      {
        id: 'sec',
        type: 'dcms.section',
        props: { background: 'alt', spacing: 'lg', width: 'normal' },
        slots: {
          default: [
            { id: 'h', type: 'dcms.heading', props: { text: 'Hello', level: '1', align: 'center' }, bind: { text: 'title' } },
            {
              id: 'row',
              type: 'dcms.stack',
              props: { direction: 'horizontal', gap: 'md', align: 'stretch', justify: 'start', wrap: false },
              slots: {
                default: [
                  {
                    id: 'b',
                    type: 'dcms.button',
                    props: { label: 'Contact', variant: 'primary', size: 'md' },
                    action: { type: 'navigate', to: '/contact' },
                  },
                ],
              },
            },
          ],
        },
      },
    ],
  },
};

describe('toGrapes → fromGrapes', () => {
  it('returns a canonical page byte for byte', () => {
    const root = load(page);
    expect(JSON.stringify(fromGrapes(root), null, 2)).toBe(JSON.stringify(page, null, 2));
  });

  it('keeps a node of an unknown type verbatim, children and all', () => {
    const ghost: Node = { id: 'g', type: 'acme.carousel', props: { speed: 3 }, slots: { items: [{ id: 'x', type: 'dcms.text' }] } };
    const tree: Node = { id: 'root', type: 'dcms.page', slots: { default: [ghost] } };
    expect(fromGrapes(load(tree))).toEqual(tree);
  });

  it('keeps children of a slot the component no longer declares', () => {
    const stale: Node = {
      id: 'h',
      type: 'dcms.heading',
      props: { text: 'x' },
      slots: { legacy: [{ id: 't', type: 'dcms.text', props: { text: 'kept' } }] },
    };
    const tree: Node = { id: 'root', type: 'dcms.page', slots: { default: [stale] } };
    expect(fromGrapes(load(tree))).toEqual(tree);
  });

  it('gives a pasted copy its own id, on the model as well as in the output', () => {
    const root = load(page);
    const slot = root.components().at(0);
    const section = slot.components().at(0);
    slot.append(section.clone());

    const ids = new Set<string>();
    const out = fromGrapes(root, ids);
    const [first, second] = out.slots!.default!;
    expect(first!.id).toBe('sec');
    expect(second!.id).not.toBe('sec');
    expect(slot.components().at(1).get(ID)).toBe(second!.id);
    // Every id in the tree is unique, nested ones included.
    expect(ids.size).toBe(JSON.stringify(out).match(/"id":/g)!.length);
  });

  it('fills in a dropped component: id, default props and its declared slots', () => {
    const root = load({ id: 'root', type: 'dcms.page' });
    const [added] = root.components().at(0).append({ type: 'dcms.stack' });
    expect(added!.get(ID)).toMatch(/^n[a-z0-9]{8}$/);
    expect(added!.get(PROPS)).toMatchObject({ direction: 'vertical', gap: 'md' });
    expect(added!.components().models.map((c) => c.get(SLOT))).toEqual(['default']);
  });
});

describe('drop rules', () => {
  function slotOf(component: Component) {
    return component.components().at(0);
  }

  it('lets a slot accept what canPlace allows and refuse what it does not', () => {
    const root = load(page);
    const body = slotOf(root);
    expect(editor.Components.canMove(body, { type: 'dcms.heading' }).result).toBe(true);
    // The page root is placed by the platform, never by a person.
    expect(editor.Components.canMove(body, { type: 'dcms.page' }).result).toBe(false);
  });

  it('refuses drops onto a component itself rather than one of its slots', () => {
    const root = load(page);
    const heading = slotOf(slotOf(root).components().at(0)).components().at(0);
    expect(editor.Components.canMove(heading, { type: 'dcms.text' }).result).toBe(false);
  });

  it('keeps the page root and the slots in place', () => {
    const root = load(page);
    expect(root.get('removable')).toBe(false);
    expect(root.get('draggable')).toBe(false);
    expect(slotOf(root).get('removable')).toBe(false);
  });
});
