import { useEffect, useRef } from 'react';
import { useTour } from './TourProvider';
import type { TourStep } from './types';

/**
 * Declares this page's tour.
 *
 * <p>Call it once at the top of a page component. Wrap the array in `useMemo` when the text
 * comes from `t()` — this hook cannot make a page cheap to render.</p>
 *
 * <p><b>Compared by value.</b> The steps go into context, which is state on a provider above
 * the calling page: a fresh array on every render re-runs the effect, which re-renders the
 * page, which builds another array. That is not a wasted comparison, it is an unbounded loop
 * that React ends with "Maximum update depth exceeded", replacing the page with an error
 * boundary. The media library shipped exactly that bug through the sibling hook
 * `useAiPageContext`, so the same guard belongs here.</p>
 *
 * <p>Identity is each step's target, title and placement. `body` is a ReactNode and cannot be
 * compared cheaply, so a body that changes while its title does not will not republish — an
 * acceptable trade, since both come from the same translation call.</p>
 */
export function usePageTour(steps: readonly TourStep[]): void {
  const { setSteps } = useTour();

  // Read through a ref so the effect can publish the current array while depending only on
  // what that array says.
  const latest = useRef(steps);
  latest.current = steps;

  const identity = steps.map((s) => `${s.target}|${s.placement ?? ''}|${s.title}`).join('\u0000');

  useEffect(() => {
    setSteps(latest.current);
    return () => setSteps([]);
  }, [identity, setSteps]);
}
