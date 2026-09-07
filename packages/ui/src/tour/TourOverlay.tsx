import { useEffect, useLayoutEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { Button } from '../ui/button';
import { cn } from '../cn';
import { useTour } from './TourProvider';

interface Box {
  top: number;
  left: number;
  width: number;
  height: number;
}

const PADDING = 6;
const CARD_WIDTH = 320;
const GAP = 12;

/**
 * The dimmed backdrop with a hole cut around the current step's target.
 *
 * <p>The cut-out is four absolutely positioned panels rather than an SVG mask or a giant
 * `box-shadow`. It sounds cruder and is better here: the panels are ordinary elements, so the
 * hole genuinely does not intercept pointer events — which means the reader can still see, and
 * scroll, the thing being explained. A mask over the whole viewport swallows every click,
 * including on the control the step is describing.</p>
 */
export function TourOverlay() {
  const { active, steps, index, next, back, stop, nodeFor, labels } = useTour();
  const step = steps[index];
  const [box, setBox] = useState<Box | null>(null);

  // Measure before paint, so the hole never appears in the wrong place for a frame.
  useLayoutEffect(() => {
    if (!active || !step) return;
    const node = nodeFor(step.target);
    if (!node) return;

    const measure = () => {
      const rect = node.getBoundingClientRect();
      setBox({ top: rect.top, left: rect.left, width: rect.width, height: rect.height });
    };
    measure();
    node.scrollIntoView({ block: 'center', behavior: 'smooth' });

    window.addEventListener('resize', measure);
    window.addEventListener('scroll', measure, true);
    return () => {
      window.removeEventListener('resize', measure);
      window.removeEventListener('scroll', measure, true);
    };
  }, [active, step, nodeFor, index]);

  useEffect(() => {
    if (!active) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') stop();
      if (event.key === 'ArrowRight') next();
      if (event.key === 'ArrowLeft') back();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [active, stop, next, back]);

  if (!active || !step || !box) return null;

  const hole = {
    top: box.top - PADDING,
    left: box.left - PADDING,
    width: box.width + PADDING * 2,
    height: box.height + PADDING * 2,
  };

  const card = cardPosition(hole, step.placement);
  const isLast = index === steps.length - 1;

  return createPortal(
    <div className="pointer-events-none fixed inset-0 z-[60]">
      {/* Four panels around the target. The gap between them is the hole. */}
      <Panel style={{ top: 0, left: 0, right: 0, height: Math.max(0, hole.top) }} onClick={stop} />
      <Panel style={{ top: hole.top, left: 0, width: Math.max(0, hole.left), height: hole.height }} onClick={stop} />
      <Panel
        style={{ top: hole.top, left: hole.left + hole.width, right: 0, height: hole.height }}
        onClick={stop}
      />
      <Panel style={{ top: hole.top + hole.height, left: 0, right: 0, bottom: 0 }} onClick={stop} />

      {/* The ring around the hole, which is what actually points at the thing. */}
      <div
        aria-hidden
        className="absolute rounded-md ring-2 ring-primary"
        style={{ top: hole.top, left: hole.left, width: hole.width, height: hole.height }}
      />

      <div
        role="dialog"
        aria-modal="false"
        aria-label={labels.dialog}
        className={cn(
          'pointer-events-auto absolute rounded-lg border bg-card p-4 shadow-xl',
          'motion-safe:animate-[dcms-enter_.16s_ease-out]',
        )}
        style={{ top: card.top, left: card.left, width: CARD_WIDTH }}
      >
        <div className="flex items-start gap-2">
          <h2 className="min-w-0 flex-1 text-sm font-semibold">{step.title}</h2>
          <button
            type="button"
            onClick={stop}
            aria-label={labels.skip}
            className="rounded p-0.5 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <X className="h-4 w-4" aria-hidden />
          </button>
        </div>

        <div className="mt-1.5 text-sm text-muted-foreground">{step.body}</div>

        <div className="mt-4 flex items-center gap-2">
          <span className="text-xs tabular-nums text-muted-foreground">
            {labels.progress(index + 1, steps.length)}
          </span>
          <div className="flex-1" />
          {index > 0 ? (
            <Button variant="ghost" size="sm" onClick={back}>
              {labels.back}
            </Button>
          ) : null}
          <Button size="sm" onClick={isLast ? stop : next}>
            {isLast ? labels.done : labels.next}
          </Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

function Panel({ style, onClick }: { style: React.CSSProperties; onClick: () => void }) {
  return (
    <div
      aria-hidden
      onClick={onClick}
      style={style}
      className="pointer-events-auto absolute bg-black/50 motion-safe:transition-[top,left,width,height] motion-safe:duration-200"
    />
  );
}

/**
 * Where the card goes.
 *
 * Preferring below, then above, then beside — and clamped to the viewport, because a card that
 * is half off-screen is worse than one on the "wrong" side of what it describes.
 */
function cardPosition(hole: Box, placement?: string): { top: number; left: number } {
  const height = 180; // generous estimate; the clamp below absorbs the error
  const viewportWidth = window.innerWidth;
  const viewportHeight = window.innerHeight;

  const below = hole.top + hole.height + GAP;
  const above = hole.top - height - GAP;

  let top = placement === 'top' && above > 0 ? above : below;
  if (top + height > viewportHeight) top = above > 0 ? above : Math.max(GAP, viewportHeight - height - GAP);

  let left = hole.left;
  if (placement === 'right') left = hole.left + hole.width + GAP;
  if (placement === 'left') left = hole.left - CARD_WIDTH - GAP;

  left = Math.min(Math.max(GAP, left), viewportWidth - CARD_WIDTH - GAP);
  return { top: Math.max(GAP, top), left };
}
