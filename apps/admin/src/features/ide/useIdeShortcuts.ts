import { useEffect, useRef } from 'react';

/** One binding: a key, whether Shift is required, and what it does. */
export interface Binding {
  key: string;
  shift?: boolean;
  run: () => void;
}

/**
 * The editor's keyboard bindings.
 *
 * <p><b>Meta or Control, always both.</b> Every binding here is "the platform modifier", and
 * the platform modifier is whichever one the person's keyboard has — a Mac keyboard plugged
 * into Linux is ordinary, and a shortcut that answers to only one of them is a bug that
 * presents as "sometimes it doesn't work".</p>
 *
 * <p><b>Captured, and default-prevented.</b> `⌘P` is Print and `⌘S` is Save Page As; letting
 * either through would put a browser dialog over the editor. Capture phase, so Monaco — which
 * installs its own document-level handlers — does not swallow them first.</p>
 *
 * <p>Bindings are read through a ref so that changing one does not re-register the listener.
 * Every handler here closes over component state, so without that the listener would be
 * removed and re-added on every render, and a keystroke arriving mid-swap would be lost.</p>
 */
export function useIdeShortcuts(bindings: readonly Binding[], enabled = true): void {
  const latest = useRef(bindings);
  latest.current = bindings;

  useEffect(() => {
    if (!enabled) return;

    const onKey = (event: KeyboardEvent) => {
      if (!event.metaKey && !event.ctrlKey) return;
      if (event.altKey) return;

      const key = event.key.toLowerCase();
      for (const binding of latest.current) {
        if (binding.key !== key) continue;
        if (!!binding.shift !== event.shiftKey) continue;

        event.preventDefault();
        event.stopPropagation();
        binding.run();
        return;
      }
    };

    document.addEventListener('keydown', onKey, true);
    return () => document.removeEventListener('keydown', onKey, true);
  }, [enabled]);
}
