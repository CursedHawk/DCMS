import type { Component, Editor } from 'grapesjs';
import { EXTRA, PROPS, type NodeExtra } from './tree';

/** The prop each kind of part lets the author type straight onto the canvas. */
export const INLINE_TEXT: Readonly<Record<string, string>> = {
  'dcms.heading': 'text',
  'dcms.text': 'text',
  'dcms.button': 'label',
  'dcms.quote': 'text',
  'dcms.badge': 'text',
};

/** The element showing `value` — the deepest one whose whole text it is. */
function elementShowing(root: HTMLElement, value: string): HTMLElement | null {
  let found: HTMLElement | null = null;
  const visit = (el: HTMLElement) => {
    if (el.textContent?.trim() !== value) return;
    found = el;
    for (const child of el.children) visit(child as HTMLElement);
  };
  visit(root);
  return found;
}

/**
 * Double-click a heading, text, button or quote to type into it in place (Mode D v2, U3.3).
 * Plain text: Enter or leaving the field keeps the change, Escape drops it, and a paste
 * arrives as text. The change is one prop edit, so it undoes like any other. A text bound to
 * content is not edited here — it comes from the item.
 *
 * `remount` redraws the component from scratch once editing ends: what the browser did to the
 * DOM while it was editable is not something React can reconcile.
 */
export function startInlineEdit(editor: Editor, model: Component, root: HTMLElement, remount: () => void): boolean {
  const prop = INLINE_TEXT[model.get('type') as string];
  const props = (model.get(PROPS) ?? {}) as Record<string, unknown>;
  const bound = ((model.get(EXTRA) ?? {}) as NodeExtra).bind?.[prop ?? ''];
  if (!prop || bound || typeof props[prop] !== 'string' || !(props[prop] as string).trim()) return false;
  const before = props[prop] as string;
  const el = elementShowing(root, before.trim());
  if (!el) return false;

  const em = editor.getModel();
  const doc = el.ownerDocument;
  el.contentEditable = 'true';
  el.spellcheck = true;
  el.style.outline = '2px solid var(--dcms-color-brand, #2563eb)';
  el.style.outlineOffset = '2px';
  el.style.cursor = 'text';
  em.setEditing(true);
  el.focus();
  doc.getSelection()?.selectAllChildren(el);

  let done = false;
  const finish = (keep: boolean) => {
    if (done) return;
    done = true;
    el.removeEventListener('keydown', onKey);
    el.removeEventListener('paste', onPaste);
    el.removeEventListener('blur', onBlur);
    const after = (el.textContent ?? '').replace(/\s+/g, ' ').trim();
    el.removeAttribute('contenteditable');
    em.setEditing(false);
    remount();
    if (keep && after && after !== before) model.set(PROPS, { ...(model.get(PROPS) as object), [prop]: after });
  };
  const onKey = (e: KeyboardEvent) => {
    // The canvas's own keys (Delete, Escape…) are not for the text being typed.
    e.stopPropagation();
    if (e.key === 'Enter') {
      e.preventDefault();
      finish(true);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      finish(false);
    }
  };
  const onPaste = (e: ClipboardEvent) => {
    e.preventDefault();
    doc.execCommand('insertText', false, e.clipboardData?.getData('text/plain').replace(/\s+/g, ' ') ?? '');
  };
  // Later, not inside the blur: React may be the one taking the element away, mid-render.
  const onBlur = () => queueMicrotask(() => finish(true));
  el.addEventListener('keydown', onKey);
  el.addEventListener('paste', onPaste);
  el.addEventListener('blur', onBlur);
  return true;
}
