import { useEffect, useRef, useState } from 'react';

/**
 * Briefly marks something that changed because the server said so.
 *
 * <p>The one class of unprompted motion this design keeps. When a row appears or a status flips
 * without the reader having done anything, a silent swap is genuinely disorienting — you look
 * back at a table and it says something different. A short highlight answers "what just
 * changed", which is information rather than decoration.</p>
 *
 * <p>Returns true for `duration` after `value` changes, and never on first render: the initial
 * paint is not a change, and flashing every row when a page loads would be exactly the
 * scattered animation this redesign is removing.</p>
 */
export function useChangeFlash(value: unknown, duration = 600): boolean {
  const [flashing, setFlashing] = useState(false);
  const previous = useRef(value);
  const first = useRef(true);

  useEffect(() => {
    if (first.current) {
      first.current = false;
      previous.current = value;
      return;
    }
    if (Object.is(previous.current, value)) return;
    previous.current = value;

    // Honour a stated preference for less motion by simply not flashing. There is nothing to
    // degrade to here: the highlight IS the motion.
    if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return;

    setFlashing(true);
    const timer = setTimeout(() => setFlashing(false), duration);
    return () => clearTimeout(timer);
  }, [value, duration]);

  return flashing;
}
