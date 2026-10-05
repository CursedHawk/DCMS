import { builtinRegistry, type Node } from '@dcms/site-runtime';
import grapesjs, { type Editor } from 'grapesjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { insertComponent } from './insert';
import { SECTION_TEMPLATES } from '../templates/sections';
import { fromGrapes, nodeFromStarter, toGrapes } from './tree';
import { registerVisualTypes } from './types';

let editor: Editor;
beforeEach(() => {
  const container = document.createElement('div');
  document.body.appendChild(container);
  editor = grapesjs.init({ container, headless: true, storageManager: false, panels: { defaults: [] }, plugins: [(e) => registerVisualTypes(e, builtinRegistry)] });
});
afterEach(() => editor.destroy());

const page: Node = {
  id: 'root',
  type: 'dcms.page',
  slots: { default: [{ id: 's', type: 'dcms.section', slots: { default: [{ id: 'h', type: 'dcms.heading' }] } }, { id: 'empty', type: 'dcms.stack' }] },
};
const load = () => {
  editor.setComponents(toGrapes(page, builtinRegistry) as never);
  return editor.getWrapper()!.components().at(0);
};
const find = (id: string) => {
  const stack = [...editor.getWrapper()!.components().models];
  while (stack.length) {
    const c = stack.shift()!;
    if (c.get('dcmsId') === id) return c;
    stack.push(...c.components().models);
  }
  throw new Error(`no ${id}`);
};
const types = (root = editor.getWrapper()!.components().at(0)) => fromGrapes(root);

describe('click to insert', () => {
  it('goes right after the selection, and selects what it added', () => {
    load();
    editor.select(find('h'));
    const out = insertComponent(editor, builtinRegistry, 'dcms.text');
    expect(out.ok).toBe(true);
    expect(types().slots!.default![0]!.slots!.default!.map((n) => n.type)).toEqual(['dcms.heading', 'dcms.text']);
    expect(editor.getSelected()).toBe(out.ok ? out.component : null);
  });

  it('goes inside a selected container that is still empty', () => {
    load();
    editor.select(find('empty'));
    insertComponent(editor, builtinRegistry, 'dcms.button');
    expect(types().slots!.default![1]!.slots!.default!.map((n) => n.type)).toEqual(['dcms.button']);
  });

  it('with nothing selected, goes at the end of the page; a refusal says why', () => {
    load();
    editor.select(undefined as never);
    insertComponent(editor, builtinRegistry, 'dcms.section');
    expect(types().slots!.default!.map((n) => n.type)).toEqual(['dcms.section', 'dcms.stack', 'dcms.section']);
    const refused = insertComponent(editor, builtinRegistry, 'dcms.page');
    expect(refused).toEqual({ ok: false, reason: 'Page cannot be placed inside another component.' });
  });

  it('a section template goes between bands: after the one holding the selection, as a whole tree', () => {
    load();
    editor.select(find('h'));
    const tree = toGrapes(nodeFromStarter(SECTION_TEMPLATES.find((t) => t.id === 'cta-band')!.tree, builtinRegistry), builtinRegistry);
    const out = insertComponent(editor, builtinRegistry, 'dcms.section', tree, { band: true });
    expect(out.ok).toBe(true);
    const page = types();
    expect(page.slots!.default!.map((n) => n.type)).toEqual(['dcms.section', 'dcms.section', 'dcms.stack']);
    expect(page.slots!.default![1]!.slots!.default![0]!.slots!.default!.map((n) => n.type)).toEqual(['dcms.heading', 'dcms.text', 'dcms.button']);
  });
});
