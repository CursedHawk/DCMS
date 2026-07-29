import { useCallback, useState } from 'react';

// A thin vertical drag handle for resizing two side-by-side panels. While the
// pointer is held it reports the horizontal movement (in px) so the caller can
// grow/shrink an adjacent panel. Double-click invokes onReset (restore default).

export function Resizer({
  onDelta,
  onReset,
  ariaLabel,
}: {
  onDelta: (dx: number) => void;
  onReset?: () => void;
  ariaLabel?: string;
}) {
  const [dragging, setDragging] = useState(false);

  const onPointerDown = useCallback(
    (e: React.PointerEvent) => {
      // Ignore anything but a primary-button drag.
      if (e.button !== 0) return;
      e.preventDefault();
      // Show the drag overlay (see below): it covers the preview <iframe> so the
      // iframe can't swallow pointer events mid-drag — otherwise the pointerup
      // lands inside the iframe, our listener never fires, and the drag "sticks".
      setDragging(true);
      let last = e.clientX;
      const move = (ev: PointerEvent) => {
        onDelta(ev.clientX - last);
        last = ev.clientX;
      };
      const end = () => {
        window.removeEventListener('pointermove', move);
        window.removeEventListener('pointerup', end);
        window.removeEventListener('pointercancel', end);
        setDragging(false);
      };
      window.addEventListener('pointermove', move);
      window.addEventListener('pointerup', end);
      window.addEventListener('pointercancel', end);
    },
    [onDelta],
  );

  return (
    <>
      <div
        role="separator"
        aria-orientation="vertical"
        aria-label={ariaLabel}
        onPointerDown={onPointerDown}
        onDoubleClick={onReset}
        className="relative z-10 -mx-0.5 w-1 shrink-0 cursor-col-resize bg-transparent transition-colors hover:bg-primary/40 active:bg-primary/60"
      />
      {/* Full-window shield during a drag: keeps every pointer event in this
          document (over iframes too) and carries the resize cursor everywhere. */}
      {dragging && <div className="fixed inset-0 z-50 cursor-col-resize select-none" />}
    </>
  );
}

/** Clamp a width to [min, max]. */
function clamp(n: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, n));
}

/**
 * A panel width persisted to localStorage under `key`, clamped to [min, max].
 * `set` accepts a value or an updater; `reset` restores the initial default.
 */
export function useStoredWidth(
  key: string,
  initial: number,
  min: number,
  max: number,
): readonly [number, (updater: number | ((w: number) => number)) => void, () => void] {
  const [width, setWidth] = useState<number>(() => {
    try {
      const raw = localStorage.getItem(key);
      const n = raw != null ? Number(raw) : NaN;
      return Number.isFinite(n) ? clamp(n, min, max) : initial;
    } catch {
      return initial;
    }
  });

  const set = useCallback(
    (updater: number | ((w: number) => number)) => {
      setWidth((w) => {
        const next = clamp(typeof updater === 'function' ? updater(w) : updater, min, max);
        try {
          localStorage.setItem(key, String(next));
        } catch {
          // ignore quota / disabled storage
        }
        return next;
      });
    },
    [key, min, max],
  );

  const reset = useCallback(() => set(initial), [set, initial]);

  return [width, set, reset] as const;
}
