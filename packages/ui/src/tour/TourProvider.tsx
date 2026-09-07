import { createContext, useCallback, useContext, useMemo, useRef, useState } from 'react';
import { DEFAULT_TOUR_LABELS, type TourLabels, type TourStep } from './types';

interface TourContextValue {
  /** The steps the current page has declared, in order. */
  steps: readonly TourStep[];
  /** Replaces the step list. Pages call this on mount, via `usePageTour`. */
  setSteps: (steps: readonly TourStep[]) => void;
  register: (id: string, node: HTMLElement | null) => void;
  nodeFor: (id: string) => HTMLElement | null;
  active: boolean;
  index: number;
  start: () => void;
  stop: () => void;
  next: () => void;
  back: () => void;
  labels: TourLabels;
}

const TourContext = createContext<TourContextValue | null>(null);

/**
 * The page tour.
 *
 * <p><b>Never starts by itself.</b> A tour that opens over the page somebody navigated to is an
 * obstacle between them and their work, and the second time it happens they learn to dismiss
 * the product rather than read it. It starts when the reader presses the launcher, and that is
 * the only way it starts.</p>
 *
 * <p>Steps are declared by the page rather than centrally, because the thing a tour explains is
 * a specific control in a specific layout — a central list would drift from the screens the
 * moment either changed, and the failure would be a tour pointing confidently at nothing.</p>
 *
 * <p>Targets register their DOM node by id. A step whose target is not on the page is skipped
 * rather than shown pointing at the corner: pages differ by permission and by what a workspace
 * has enabled, so "this step's control is not here" is an ordinary, expected state.</p>
 *
 * <p>No dependency. `driver.js`, `shepherd` and `react-joyride` are all heavier than this, and
 * none of them handles what makes this app awkward — split panes, an iframe canvas, and a
 * sidebar that becomes a drawer.</p>
 */
export function TourProvider({
  children,
  labels: partial,
}: {
  children: React.ReactNode;
  labels?: Partial<TourLabels>;
}) {
  const [steps, setStepsState] = useState<readonly TourStep[]>([]);
  const [active, setActive] = useState(false);
  const [index, setIndex] = useState(0);
  const nodes = useRef(new Map<string, HTMLElement>());

  const register = useCallback((id: string, node: HTMLElement | null) => {
    if (node) nodes.current.set(id, node);
    else nodes.current.delete(id);
  }, []);

  const nodeFor = useCallback((id: string) => nodes.current.get(id) ?? null, []);

  const setSteps = useCallback((next: readonly TourStep[]) => {
    setStepsState(next);
    // A route change replaces the steps; a tour still running would be narrating the previous
    // page over the new one.
    setActive(false);
    setIndex(0);
  }, []);

  /** Only steps whose target is actually on the page. */
  const present = useMemo(
    () => steps.filter((step) => nodes.current.has(step.target)),
    // Recomputed whenever the tour opens or moves, because registration happens during render
    // of the page and this provider does not re-render when a target mounts.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [steps, active, index],
  );

  const start = useCallback(() => {
    setIndex(0);
    setActive(true);
  }, []);
  const stop = useCallback(() => setActive(false), []);
  const next = useCallback(
    () =>
      setIndex((current) => {
        if (current + 1 >= present.length) {
          setActive(false);
          return current;
        }
        return current + 1;
      }),
    [present.length],
  );
  const back = useCallback(() => setIndex((current) => Math.max(0, current - 1)), []);

  const value = useMemo<TourContextValue>(
    () => ({
      steps: present,
      setSteps,
      register,
      nodeFor,
      active,
      index,
      start,
      stop,
      next,
      back,
      labels: { ...DEFAULT_TOUR_LABELS, ...partial },
    }),
    [present, setSteps, register, nodeFor, active, index, start, stop, next, back, partial],
  );

  return <TourContext.Provider value={value}>{children}</TourContext.Provider>;
}

export function useTour(): TourContextValue {
  const context = useContext(TourContext);
  if (!context) {
    throw new Error('useTour must be used within a TourProvider');
  }
  return context;
}

/** True when the current page has anything to show. The launcher hides itself otherwise. */
export function useTourAvailable(): boolean {
  return useTour().steps.length > 0;
}
