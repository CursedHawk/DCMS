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
  /**
   * Starts the page's tour — or, given steps, a one-off tour of those instead: a short tour of
   * one task, started from the page's own control. The page's tour is back once it ends.
   */
  start: (only?: readonly TourStep[]) => void;
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
 * the product rather than read it. It starts when the reader presses the launcher — or once,
 * on someone's very first visit, on a page that cannot be used without it (the visual builder,
 * which remembers that it has). Never on every visit.</p>
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
  const [only, setOnly] = useState<readonly TourStep[] | null>(null);
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
    setOnly(null);
    // A route change replaces the steps; a tour still running would be narrating the previous
    // page over the new one.
    setActive(false);
    setIndex(0);
  }, []);

  /** Only steps whose target is actually on the page. */
  const present = useMemo(
    () => (only ?? steps).filter((step) => nodes.current.has(step.target)),
    // Recomputed whenever the tour opens or moves, because registration happens during render
    // of the page and this provider does not re-render when a target mounts.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [steps, only, active, index],
  );

  const start = useCallback((tour?: readonly TourStep[]) => {
    setOnly(tour ?? null);
    setIndex(0);
    setActive(true);
  }, []);
  const stop = useCallback(() => {
    setActive(false);
    setOnly(null);
  }, []);
  const next = useCallback(() => {
    if (index + 1 >= present.length) stop();
    else setIndex(index + 1);
  }, [index, present.length, stop]);
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
