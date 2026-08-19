import { BIND_ATTR, EMPTY_ATTR, IF_ATTR, REPEAT_ATTR, UNLESS_ATTR } from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { useEffect } from 'react';
import { useBuilder } from '../store';

/**
 * Makes a component template readable while it is being built.
 *
 * A template is mostly *empty* elements — a heading bound to `title` has no text
 * of its own, because the text comes from the tenant's content at render time.
 * Without this the canvas shows a stack of blank boxes, and the author cannot
 * tell a heading bound to the wrong field from one bound to nothing.
 *
 * The hints are CSS only, injected into the canvas iframe: nothing is written to
 * the model, so none of it can end up in the saved template. Real content is
 * deliberately *not* substituted here — the elements on this canvas are the
 * author's to edit, and typing over a preview would silently bake one item's
 * text into the template for every item.
 */

const STYLE_ID = 'dcms-template-hints';

export function TemplateHints({ editor }: { editor: Editor | null }) {
  const editingComponent = useBuilder((s) => s.activeKind === 'component');
  const reloadToken = useBuilder((s) => s.reloadToken);

  useEffect(() => {
    if (!editor) return;

    const apply = () => {
      const doc = editor.Canvas.getDocument();
      if (!doc) return;
      const existing = doc.getElementById(STYLE_ID);
      if (!editingComponent) {
        existing?.remove();
        return;
      }
      const style = existing ?? doc.createElement('style');
      style.id = STYLE_ID;
      style.textContent = CSS;
      if (!existing) doc.head.appendChild(style);
    };

    apply();
    editor.on('canvas:frame:load component:mount', apply);
    return () => {
      editor.off('canvas:frame:load component:mount', apply);
    };
  }, [editor, editingComponent, reloadToken]);

  return null;
}

/**
 * `attr()` rather than generated labels: the hint is the binding itself, which
 * is the thing the author needs to check, and it stays correct without this
 * component knowing anything about what a binding means.
 */
const CSS = `
  [${BIND_ATTR}] {
    outline: 1px dashed rgba(59,130,246,.5);
    outline-offset: 1px;
    min-height: 1em;
    min-width: 2em;
  }
  [${BIND_ATTR}]:empty::before {
    content: attr(${BIND_ATTR});
    font: italic 12px/1.4 ui-monospace, SFMono-Regular, monospace;
    color: rgba(59,130,246,.85);
  }
  img[${BIND_ATTR}]:not([src]) {
    display: block;
    min-height: 80px;
    background: repeating-linear-gradient(45deg, rgba(59,130,246,.08) 0 8px, transparent 8px 16px);
  }
  [${REPEAT_ATTR}] {
    outline: 1px solid rgba(16,185,129,.55);
    outline-offset: 2px;
    position: relative;
  }
  [${REPEAT_ATTR}]::after {
    content: 'repeats per item';
    position: absolute;
    top: -9px;
    left: 6px;
    padding: 0 4px;
    font: 500 10px/1.4 ui-sans-serif, system-ui, sans-serif;
    color: rgb(5,150,105);
    background: #fff;
  }
  [${IF_ATTR}], [${UNLESS_ATTR}] {
    box-shadow: inset 0 0 0 1px rgba(234,179,8,.55);
  }
  [${EMPTY_ATTR}] {
    opacity: .6;
  }
`;
