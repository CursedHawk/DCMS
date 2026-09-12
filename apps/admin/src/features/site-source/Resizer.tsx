import { useCallback, useState } from 'react';
import { cn } from '@dcms/ui';

// A thin drag handle for resizing two adjacent panels. While the pointer is held it reports the
// movement (in px) along its axis so the caller can grow/shrink a neighbour. Double-click
// invokes onReset (restore default).
//
// `horizontal` is the bottom panel's: a handle lying across the layout, dragged up and down.
// Same mechanics, different axis — worth one prop rather than a second component, because the
// pointer-capture and iframe-shield behaviour below is the fiddly part and must not be
// duplicated.

export function Resizer({
  onDelta,
  onReset,
  ariaLabel,
  orientation = 'vertical',
}: {
  /** Movement along the handle's axis: px right for a vertical handle, px down for a horizontal one. */
  onDelta: (delta: number) => void;
  onReset?: () => void;
  ariaLabel?: string;
  orientation?: 'vertical' | 'horizontal';
}) {
  const [dragging, setDragging] = useState(false);
  const horizontal = orientation === 'horizontal';

  const onPointerDown = useCallback(
    (e: React.PointerEvent) => {
      // Ignore anything but a primary-button drag.
      if (e.button !== 0) return;
      e.preventDefault();
      // Show the drag overlay (see below): it covers the preview <iframe> so the
      // iframe can't swallow pointer events mid-drag — otherwise the pointerup
      // lands inside the iframe, our listener never fires, and the drag "sticks".
      setDragging(true);
      let last = horizontal ? e.clientY : e.clientX;
      const move = (ev: PointerEvent) => {
        const now = horizontal ? ev.clientY : ev.clientX;
        onDelta(now - last);
        last = now;
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
    [onDelta, horizontal],
  );

  return (
    <>
      <div
        role="separator"
        // ARIA's axis is the separator's own orientation, which is the opposite of the axis it
        // is dragged along: a handle you drag up and down lies horizontally.
        aria-orientation={horizontal ? 'horizontal' : 'vertical'}
        aria-label={ariaLabel}
        onPointerDown={onPointerDown}
        onDoubleClick={onReset}
        className={cn(
          'relative z-10 shrink-0 bg-transparent transition-colors hover:bg-primary/40 active:bg-primary/60',
          horizontal ? '-my-0.5 h-1 cursor-row-resize' : '-mx-0.5 w-1 cursor-col-resize',
        )}
      />
      {/* Full-window shield during a drag: keeps every pointer event in this
          document (over iframes too) and carries the resize cursor everywhere. */}
      {dragging && (
        <div
          className={cn(
            'fixed inset-0 z-50 select-none',
            horizontal ? 'cursor-row-resize' : 'cursor-col-resize',
          )}
        />
      )}
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
