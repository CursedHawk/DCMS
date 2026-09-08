import { useEffect, useState } from 'react';

/**
 * Whether a CSS media query matches, kept current as the window changes.
 *
 * <p>For the handful of cases a media query in the stylesheet cannot answer, because what
 * changes is <em>which component renders</em> rather than how one looks — the IDE below the
 * desktop breakpoint is the reason this exists. Anything that is only a matter of layout
 * belongs in Tailwind's breakpoints, where it costs no JavaScript and no re-render.</p>
 *
 * <p><b>Reads once on mount rather than during render.</b> `matchMedia` does not exist during
 * server rendering or in some test environments, and a hook that throws there would make a
 * component untestable for a reason that has nothing to do with it. The first paint therefore
 * assumes no match, which for a desktop gate is the safe direction: a wide screen sees its
 * panel for one frame; a narrow one never sees a layout it cannot hold.</p>
 */
export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(false);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;

    const mq = window.matchMedia(query);
    // Read through the list rather than off a captured value: a query that already matched
    // when the effect ran must not wait for a resize to say so.
    const sync = () => setMatches(mq.matches);
    sync();

    mq.addEventListener('change', sync);
    return () => mq.removeEventListener('change', sync);
  }, [query]);

  return matches;
}

/** The `lg` breakpoint, which is where this design system stops being a single column. */
export const DESKTOP_QUERY = '(min-width: 1024px)';

/** True on a screen wide enough for a two-pane editor. */
export function useIsDesktop(): boolean {
  return useMediaQuery(DESKTOP_QUERY);
}
