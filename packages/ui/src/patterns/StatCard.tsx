import { ArrowDown, ArrowUp, Minus } from 'lucide-react';
import { cn } from '../cn';
import { Skeleton } from '../ui/skeleton';
import { InfoHint } from './InfoHint';

/**
 * One number, said properly.
 *
 * <p>The label goes above the value at small size, because the number is what the eye should
 * land on. A trend is shown only when the caller supplies a comparison period — a percentage
 * with nothing to compare against is decoration, and an arrow with no baseline is worse
 * because it looks like information.</p>
 *
 * <p>Direction is not colour alone: the arrow carries it too, and `goodDirection` exists
 * because a rise is not automatically good. More visitors is; more failed builds is not.</p>
 */
export function StatCard({
  label,
  value,
  hint,
  hintTitle,
  delta,
  deltaLabel,
  goodDirection = 'up',
  isLoading,
  footer,
  className,
}: {
  label: string;
  value: React.ReactNode;
  /** A sentence explaining what this number counts, behind an "i". */
  hint?: React.ReactNode;
  hintTitle?: string;
  /** Fractional change against the comparison period. 0.12 is +12%. */
  delta?: number | null;
  /** What the delta is measured against — "vs previous 30 days". */
  deltaLabel?: string;
  goodDirection?: 'up' | 'down' | 'neutral';
  isLoading?: boolean;
  footer?: React.ReactNode;
  className?: string;
}) {
  const hasDelta = typeof delta === 'number' && Number.isFinite(delta);
  const rising = hasDelta && delta > 0;
  const flat = hasDelta && delta === 0;
  const Arrow = flat ? Minus : rising ? ArrowUp : ArrowDown;

  const tone =
    !hasDelta || flat || goodDirection === 'neutral'
      ? 'text-muted-foreground'
      : (rising && goodDirection === 'up') || (!rising && goodDirection === 'down')
        ? 'text-[hsl(var(--success))]'
        : 'text-destructive';

  return (
    <div className={cn('rounded-lg border bg-card p-4', className)}>
      <div className="flex items-center gap-1.5">
        <p className="text-sm text-muted-foreground">{label}</p>
        {hint ? (
          <InfoHint title={hintTitle} label={`About ${label}`}>
            {hint}
          </InfoHint>
        ) : null}
      </div>

      {isLoading ? (
        <Skeleton className="mt-2 h-8 w-24" />
      ) : (
        <p className="mt-1 text-2xl font-semibold tracking-tight">{value}</p>
      )}

      {hasDelta && !isLoading ? (
        <p className={cn('mt-1 flex items-center gap-1 text-xs', tone)}>
          <Arrow className="h-3 w-3" aria-hidden />
          <span>
            {flat ? '0%' : `${rising ? '+' : ''}${Math.round(delta * 100)}%`}
          </span>
          {deltaLabel ? <span className="text-muted-foreground">{deltaLabel}</span> : null}
        </p>
      ) : null}

      {footer ? <div className="mt-2 text-xs text-muted-foreground">{footer}</div> : null}
    </div>
  );
}
