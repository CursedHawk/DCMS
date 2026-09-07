export interface TourStep {
  /** Matches the `id` on a `<TourTarget>`. A step whose target is absent is skipped. */
  target: string;
  title: string;
  body: React.ReactNode;
  /** Where the card sits relative to the target. Falls back automatically when it will not fit. */
  placement?: 'top' | 'right' | 'bottom' | 'left';
}

export interface TourLabels {
  next: string;
  back: string;
  done: string;
  skip: string;
  progress: (current: number, total: number) => string;
  /** The accessible name of the launcher. */
  start: string;
  dialog: string;
}

export const DEFAULT_TOUR_LABELS: TourLabels = {
  next: 'Next',
  back: 'Back',
  done: 'Done',
  skip: 'Skip',
  progress: (current, total) => `${current} of ${total}`,
  start: 'Show me around this page',
  dialog: 'Page tour',
};
