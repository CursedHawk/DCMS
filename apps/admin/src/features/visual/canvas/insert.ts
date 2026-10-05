import { canPlace, type Registry } from '@dcms/site-runtime';
import type { Component, Editor } from 'grapesjs';
import { SLOT, SLOT_TYPE } from './tree';

export type Insertion = { ok: true; component: Component } | { ok: false; reason: string };

/**
 * Add a component by clicking it in the palette rather than dragging it: right after what is
 * selected, inside a selected slot, or — with nothing selected — at the end of the page. The
 * same `canPlace` rule a drop obeys decides, and a refusal comes back with its reason.
 *
 * `content` is what the palette would drop (`{ type }`, or a whole tree for a section
 * template); the component types fill in ids, defaults and slots as they would for a drop.
 */
export function insertComponent(editor: Editor, registry: Registry, type: string, content: object = { type }, { band = false } = {}): Insertion {
  const selected = editor.getSelected();
  const root = editor.getWrapper()?.components().at(0);
  const pageSlot = root?.components().models.find((c) => c.get('type') === SLOT_TYPE);
  const candidates: { slot: Component; at?: number }[] = [];

  if (band) {
    // A whole section of the page goes between the page's bands: after the one holding the
    // selection, never inside it.
    let top = selected;
    while (top && top.parent() && top.parent() !== pageSlot) top = top.parent();
    if (pageSlot && top?.parent() === pageSlot) candidates.push({ slot: pageSlot, at: pageSlot.components().indexOf(top) + 1 });
  } else if (selected?.get('type') === SLOT_TYPE) {
    candidates.push({ slot: selected });
  } else if (selected) {
    const parent = selected.parent();
    if (parent?.get('type') === SLOT_TYPE) candidates.push({ slot: parent, at: parent.components().indexOf(selected) + 1 });
    // A selected container whose content is empty: put it inside instead.
    const own = selected.components().models.filter((c) => c.get('type') === SLOT_TYPE);
    if (own.length && own.every((s) => s.components().length === 0)) candidates.unshift({ slot: own[0]! });
  }
  if (pageSlot) candidates.push({ slot: pageSlot });

  let reason = 'There is nowhere on this page to add it.';
  for (const { slot, at } of candidates) {
    const owner = slot.parent();
    if (!owner) continue;
    const placement = canPlace(registry, owner.get('type') as string, slot.get(SLOT) as string, type, slot.components().length);
    if (!placement.ok) {
      reason = placement.reason;
      continue;
    }
    const [added] = slot.append(content as never, at === undefined ? {} : { at });
    if (!added) continue;
    editor.select(added);
    added.getEl()?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    return { ok: true, component: added };
  }
  return { ok: false, reason };
}
