import { meterState } from '@dcms/core';
import { cn } from '../cn';

/**
 * A quantity against its ceiling.
 *
 * <p>The platform console's signature device, moved here so the admin's storage quota can use
 * the same one. The reason it is not a progress bar: every quantity in these consoles means
 * something only against its limit. "3.1 GB" is not information; "3.1 of 8 GB" is.</p>
 *
 * <p>The ceiling is drawn as a hard edge rather than implied by the end of the track, so a
 * meter at 96% and a meter at 100% are distinguishable at a glance — which is the difference
 * between a warning and an outage.</p>
 *
 * <p>Colour follows the thresholds the platform's own alerts use (80% watch, 90% critical), so
 * the colour here and the alert that wakes somebody agree. Colour is never the only signal:
 * the value is always written out beside it.</p>
 */
export function Meter({
  fraction,
  label,
  value,
  className,
  size = 'md',
}: {
  /** 0–1. Values above 1 clamp visually but still read as critical. */
  fraction: number;
  label?: React.ReactNode;
  /** The number, written out. Required in spirit: colour alone is not an accessible signal. */
  value?: React.ReactNode;
  className?: string;
  size?: 'sm' | 'md';
}) {
  const safe = Number.isFinite(fraction) ? Math.max(0, fraction) : 0;
  const state = meterState(safe);
  const percent = Math.round(safe * 100);

  return (
    <div className={cn('space-y-1', className)}>
      {label || value ? (
        <div className="flex items-baseline justify-between gap-3 text-sm">
          {label ? <span className="min-w-0 truncate text-muted-foreground">{label}</span> : null}
          {value ? <span className="shrink-0 font-medium">{value}</span> : null}
        </div>
      ) : null}
      <div
        role="meter"
        aria-valuenow={percent}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-label={typeof label === 'string' ? label : undefined}
        className={cn(
          'relative overflow-hidden rounded-sm bg-muted',
          size === 'sm' ? 'h-1.5' : 'h-2',
        )}
      >
        <div
          className={cn(
            'h-full rounded-l-sm transition-[width] duration-500',
            state === 'critical'
              ? 'bg-destructive'
              : state === 'watch'
                ? 'bg-[hsl(var(--warning))]'
                : 'bg-primary',
          )}
          style={{ width: `${Math.min(100, percent)}%` }}
        />
        {/* The ceiling: a place, rather than an absence of remaining track. */}
        <span
          aria-hidden
          className="absolute inset-y-0 right-0 w-0.5 bg-foreground/35"
        />
      </div>
    </div>
  );
}
