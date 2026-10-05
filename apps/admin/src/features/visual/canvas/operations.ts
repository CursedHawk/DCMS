import { canPlace, type Registry } from '@dcms/site-runtime';
import type { Component, Editor } from 'grapesjs';
import { ID, PROPS, SLOT, SLOT_TYPE } from './tree';

/**
 * What the shortcuts, the right-click menu and the breadcrumb do to the selection (Mode D v2,
 * U3). Each is one undoable canvas edit, obeys the same `canPlace` rule as a drop, and returns
 * why when it cannot be done.
 */
export type Outcome = { ok: true } | { ok: false; reason: string };

const OK: Outcome = { ok: true };
const no = (reason: string): Outcome => ({ ok: false, reason });

/** The canvas component for a node id. */
export function findNode(editor: Editor, id: string): Component | undefined {
  const stack = [...(editor.getWrapper()?.components().models ?? [])];
  while (stack.length) {
    const c = stack.pop()!;
    if (c.get(ID) === id) return c;
    stack.push(...c.components().models);
  }
  return undefined;
}

/** A component node on the canvas (not a slot, not the page itself). */
function isPlaced(c: Component | undefined): c is Component {
  return !!c && c.get('type') !== SLOT_TYPE && c.parent()?.get('type') === SLOT_TYPE;
}

/** The node whose slot holds `c` — its parent as the author sees it. */
export function parentNode(c: Component): Component | undefined {
  const slot = c.parent();
  return slot?.get('type') === SLOT_TYPE ? slot.parent() : undefined;
}

/** The selection's ancestors, outermost first, ending with the selection: the breadcrumb. */
export function trail(c: Component | undefined): Component[] {
  const out: Component[] = [];
  for (let at = c; at && at.get('type') !== 'wrapper'; at = at.get('type') === SLOT_TYPE ? at.parent() : parentNode(at)) {
    if (at.get('type') !== SLOT_TYPE) out.unshift(at);
  }
  return out;
}

/** May `type` go into `slot` beside `siblings` others? */
const fits = (registry: Registry, slot: Component, type: string, siblings: number) =>
  canPlace(registry, slot.parent()!.get('type') as string, slot.get(SLOT) as string, type, siblings);

export function selectParent(editor: Editor): Outcome {
  const selected = editor.getSelected();
  const parent = selected && parentNode(selected);
  if (!parent || parent.get('selectable') === false) return no('This is already the outermost part.');
  editor.select(parent);
  return OK;
}

export function duplicate(editor: Editor, registry: Registry): Outcome {
  const selected = editor.getSelected();
  if (!isPlaced(selected) || selected.get('copyable') === false) return no('This cannot be copied.');
  const slot = selected.parent()!;
  const placement = fits(registry, slot, selected.get('type') as string, slot.components().length);
  if (!placement.ok) return no(placement.reason);
  const [copy] = slot.append(selected.clone(), { at: selected.index() + 1 });
  if (copy) editor.select(copy);
  return OK;
}

export function remove(editor: Editor): Outcome {
  const selected = editor.getSelected();
  if (!isPlaced(selected) || selected.get('removable') === false) return no('This cannot be deleted.');
  const slot = selected.parent()!;
  const index = selected.index();
  selected.remove();
  // Keep a place to continue from: the next part, the previous one, or the container.
  editor.select(slot.components().at(index) ?? slot.components().at(index - 1) ?? parentNode(slot.parent()!) ?? slot.parent());
  return OK;
}

/** One step earlier (-1) or later (+1) among its neighbours. */
export function move(editor: Editor, by: -1 | 1): Outcome {
  const selected = editor.getSelected();
  if (!isPlaced(selected) || selected.get('draggable') === false) return no('This cannot be moved.');
  const slot = selected.parent()!;
  const index = selected.index();
  const to = index + by;
  if (to < 0 || to >= slot.components().length) return no(by < 0 ? 'It is already first.' : 'It is already last.');
  // GrapesJS counts the destination before removing the component from where it was.
  selected.move(slot, { at: by > 0 ? to + 1 : to });
  editor.select(selected);
  return OK;
}

/** Put the selection inside a new container of `type`, where the selection was. */
export function wrap(editor: Editor, registry: Registry, type: string): Outcome {
  const selected = editor.getSelected();
  if (!isPlaced(selected) || selected.get('draggable') === false) return no('This cannot be moved.');
  const slot = selected.parent()!;
  const outer = fits(registry, slot, type, slot.components().length - 1);
  if (!outer.ok) return no(outer.reason);
  const def = registry.get(type);
  const fitsIn = (s: { name: string }) => canPlace(registry, type, s.name, selected.get('type') as string, 0).ok;
  const inner = def?.slots?.find((s) => s.name === 'default' && fitsIn(s)) ?? def?.slots?.find(fitsIn);
  if (!inner) return no(`${def?.label ?? type} cannot hold ${selected.getName()}.`);

  const [wrapper] = slot.append({ type } as never, { at: selected.index() });
  const target = wrapper?.components().models.find((s) => s.get(SLOT) === inner.name);
  if (!wrapper || !target) return no('It could not be wrapped.');
  // A fresh container brings its starter children; wrapping replaces them with the selection.
  target.components().reset();
  selected.move(target, { at: 0 });
  editor.select(wrapper);
  return OK;
}

/** Replace the selection by what is inside it. */
export function unwrap(editor: Editor, registry: Registry): Outcome {
  const selected = editor.getSelected();
  if (!isPlaced(selected) || selected.get('removable') === false) return no('This cannot be removed.');
  const children = selected.components().models.flatMap((s) => s.components().models);
  if (!children.length) return no('There is nothing inside to keep.');
  const slot = selected.parent()!;
  for (const [i, child] of children.entries()) {
    const placement = fits(registry, slot, child.get('type') as string, slot.components().length - 1 + i);
    if (!placement.ok) return no(placement.reason);
  }
  let at = selected.index();
  for (const child of children) child.move(slot, { at: at++ });
  selected.remove();
  editor.select(children[0]);
  return OK;
}

/** Style and layout settings, copied from one part to paste onto others. */
let copiedStyle: { type: string; props: Record<string, unknown> } | null = null;

const STYLE_GROUPS = new Set(['style', 'layout']);

export function copyStyle(editor: Editor, registry: Registry): Outcome {
  const selected = editor.getSelected();
  const def = selected && registry.get(selected.get('type') as string);
  if (!selected || !def) return no('Select a part first.');
  const props = (selected.get(PROPS) ?? {}) as Record<string, unknown>;
  const names = def.props.filter((p) => p.group && STYLE_GROUPS.has(p.group)).map((p) => p.name);
  copiedStyle = { type: def.type, props: Object.fromEntries(names.filter((n) => n in props).map((n) => [n, props[n]])) };
  return OK;
}

export function canPasteStyle(): boolean {
  return copiedStyle !== null;
}

/** Paste what applies: settings the selected kind of part also has, with values it allows. */
export function pasteStyle(editor: Editor, registry: Registry): Outcome {
  const selected = editor.getSelected();
  const def = selected && registry.get(selected.get('type') as string);
  if (!selected || !def || !copiedStyle) return no('Copy a style first.');
  const props = { ...((selected.get(PROPS) ?? {}) as Record<string, unknown>) };
  let pasted = 0;
  for (const p of def.props) {
    if (!(p.name in copiedStyle.props) || !p.group || !STYLE_GROUPS.has(p.group)) continue;
    const value = copiedStyle.props[p.name];
    if ('options' in p && !p.options.some((o) => o.value === value)) continue;
    props[p.name] = value;
    pasted++;
  }
  if (!pasted) return no(`Nothing copied applies to ${def.label}.`);
  selected.set(PROPS, props);
  return OK;
}
