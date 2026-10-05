import { builtinRegistry, type Node } from '@dcms/site-runtime';
import grapesjs, { type Editor } from 'grapesjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { copyStyle, duplicate, move, pasteStyle, remove, selectParent, trail, unwrap, wrap } from './operations';
import { fromGrapes, toGrapes } from './tree';
import { registerVisualTypes } from './types';

let editor: Editor;
beforeEach(() => {
  const container = document.createElement('div');
  document.body.appendChild(container);
  editor = grapesjs.init({ container, headless: true, storageManager: false, panels: { defaults: [] }, plugins: [(e) => registerVisualTypes(e, builtinRegistry)] });
  const page: Node = {
    id: 'root',
    type: 'dcms.page',
    slots: {
      default: [
        { id: 's', type: 'dcms.section', props: { background: 'alt' }, slots: { default: [{ id: 'a', type: 'dcms.heading' }, { id: 'b', type: 'dcms.text' }] } },
        { id: 't', type: 'dcms.section' },
      ],
    },
  };
  editor.setComponents(toGrapes(page, builtinRegistry) as never);
});
afterEach(() => editor.destroy());

const find = (id: string) => {
  const stack = [...editor.getWrapper()!.components().models];
  while (stack.length) {
    const c = stack.shift()!;
    if (c.get('dcmsId') === id) return c;
    stack.push(...c.components().models);
  }
  throw new Error(`no ${id}`);
};
const tree = () => fromGrapes(editor.getWrapper()!.components().at(0));
const kids = (n: Node) => (n.slots?.default ?? []).map((c) => c.id);

describe('canvas operations', () => {
  it('walks up, and draws the breadcrumb from the page down', () => {
    editor.select(find('b'));
    expect(trail(find('b')).map((c) => c.get('dcmsId'))).toEqual(['root', 's', 'b']);
    expect(selectParent(editor)).toEqual({ ok: true });
    expect(editor.getSelected()?.get('dcmsId')).toBe('s');
  });

  it('moves among neighbours and says when it cannot', () => {
    editor.select(find('a'));
    expect(move(editor, 1)).toEqual({ ok: true });
    expect(kids(tree().slots!.default![0]!)).toEqual(['b', 'a']);
    expect(move(editor, 1)).toEqual({ ok: false, reason: 'It is already last.' });
    move(editor, -1);
    expect(kids(tree().slots!.default![0]!)).toEqual(['a', 'b']);
  });

  it('duplicates beside, with a new id, and deletes keeping a selection', () => {
    editor.select(find('a'));
    duplicate(editor, builtinRegistry);
    const ids = kids(tree().slots!.default![0]!);
    expect(ids).toHaveLength(3);
    expect(ids[1]).not.toBe('a');
    remove(editor);
    expect(kids(tree().slots!.default![0]!)).toEqual(['a', 'b']);
    expect(editor.getSelected()?.get('dcmsId')).toBe('b');
  });

  it('wraps in a container and unwraps again; a container that cannot go there is refused', () => {
    editor.select(find('a'));
    expect(wrap(editor, builtinRegistry, 'dcms.stack')).toEqual({ ok: true });
    const section = tree().slots!.default![0]!;
    expect(section.slots!.default!.map((n) => n.type)).toEqual(['dcms.stack', 'dcms.text']);
    expect(kids(section.slots!.default![0]!)).toEqual(['a']);
    expect(unwrap(editor, builtinRegistry)).toEqual({ ok: true });
    expect(kids(tree().slots!.default![0]!)).toEqual(['a', 'b']);
    expect(wrap(editor, builtinRegistry, 'dcms.page').ok).toBe(false);
  });

  it('copies style settings and pastes the ones that apply', () => {
    editor.select(find('s'));
    copyStyle(editor, builtinRegistry);
    editor.select(find('t'));
    expect(pasteStyle(editor, builtinRegistry)).toEqual({ ok: true });
    expect(tree().slots!.default![1]!.props!.background).toBe('alt');
  });
});
