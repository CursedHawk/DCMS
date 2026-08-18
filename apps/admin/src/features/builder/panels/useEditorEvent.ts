import type { Editor } from 'grapesjs';
import { useEffect, useReducer, useState } from 'react';

/**
 * Bridges GrapesJS's Backbone event bus to React.
 *
 * The panels run in `custom: true` mode, which means GrapesJS keeps all the
 * state and expects *someone* to draw it. Rather than rendering into the DOM
 * node it offers (which would need a portal and put the panels outside the
 * admin's component tree), the panels read the manager APIs directly and use
 * these hooks to know when to re-read.
 */

/** Re-render whenever any of the given space-separated GrapesJS events fire. */
export function useEditorEvent(editor: Editor | null, events: string): number {
  const [tick, bump] = useReducer((n: number) => n + 1, 0);

  useEffect(() => {
    if (!editor) return;
    editor.on(events, bump);
    return () => {
      editor.off(events, bump);
    };
  }, [editor, events]);

  return tick;
}

/**
 * Capture the payload of a GrapesJS `*:custom` event.
 *
 * These carry the callbacks a custom UI needs — the block drag handles, the
 * asset select/close pair — and fire whenever the manager wants its UI redrawn.
 *
 * The catch is *when* they fire. The block manager emits on any change to its
 * collection, which means its only emission happens while the plugins register
 * their blocks during `grapesjs.init` — before the editor object exists for
 * React to store, let alone before a panel has mounted and subscribed. A
 * listener alone therefore waits forever, which is exactly what left the block
 * palette stuck on "Loading…".
 *
 * So `seed` reads the manager's current payload once on subscribe. Subscribing
 * still matters for everything after that.
 */
export function useCustomPayload<T>(
  editor: Editor | null,
  event: string,
  seed?: (editor: Editor) => T | null,
): T | null {
  const [payload, setPayload] = useState<T | null>(null);

  useEffect(() => {
    if (!editor) return;
    const handler = (data: T) => setPayload(data);
    editor.on(event, handler);

    const initial = seed?.(editor) ?? null;
    if (initial) setPayload(initial);

    return () => {
      editor.off(event, handler);
    };
    // `seed` is a module-level function at every call site; re-subscribing on an
    // unstable identity would drop events.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editor, event]);

  return payload;
}

/** The currently selected component, tracked across selection changes. */
export function useSelected(editor: Editor | null) {
  const tick = useEditorEvent(editor, 'component:toggled component:selected component:deselected');
  // `tick` is the dependency: the selection itself is read fresh each render so
  // a mutated component (renamed, restyled) is never stale.
  void tick;
  return editor?.getSelected() ?? undefined;
}
