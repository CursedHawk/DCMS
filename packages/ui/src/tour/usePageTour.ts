import { useEffect } from 'react';
import { useTour } from './TourProvider';
import type { TourStep } from './types';

/**
 * Declares this page's tour.
 *
 * <p>Call it once at the top of a page component with a stable array — the steps go into
 * context, and passing a fresh array literal every render would reset the tour on every
 * keystroke. Wrap it in `useMemo` when the text comes from `t()`.</p>
 */
export function usePageTour(steps: readonly TourStep[]): void {
  const { setSteps } = useTour();
  useEffect(() => {
    setSteps(steps);
    return () => setSteps([]);
  }, [steps, setSteps]);
}
