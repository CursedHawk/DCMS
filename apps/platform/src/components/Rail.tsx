import { cn } from '@dcms/admin-ui';
import { ofLimit, railState } from '../lib/format';

/**
 * A quantity against its ceiling.
 *
 * The console's one recurring data mark, and the reason it exists rather than a progress bar:
 * almost nothing here means anything on its own. "3.1 GB" is not information. "3.1 of 8 GB,
 * over a 30-day window" tells an operator whether to act, and roughly when they will have to.
 *
 * `window` is the retention or sampling period the number covers. It is a separate prop rather
 * than part of the label because it is the half people forget: a store at 40% of its budget is
 * fine over 30 days and alarming over 6 hours.
 */
export function Rail({
  label,
  used,
  limit,
  window,
  hint,
  className,
}: {
  label: string;
  used: number;
  limit: number;
  /** The period the figure covers — "30-day window", "7 days". */
  window?: string;
  /** Anything the number alone would mislead about. */
  hint?: string;
  className?: string;
}) {
  const fraction = limit > 0 ? Math.min(used / limit, 1) : 0;
  const state = railState(fraction);
  const percent = Math.round(fraction * 100);

  return (
    <div
      className={cn(
        // Stacks below sm: at 420px the three-column form squeezed the value column until
        // "0.0 / 40.0 GB" broke one character per line.
        'grid grid-cols-[1fr_auto] items-center gap-x-4 gap-y-1',
        'sm:grid-cols-[minmax(6rem,9rem)_1fr_auto]',
        className,
      )}
    >
      <span className="truncate text-sm text-foreground">{label}</span>

      <div
        className="rail col-span-2 sm:col-span-1 sm:order-none order-last"
        role="meter"
        aria-label={label}
        aria-valuenow={percent}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuetext={`${ofLimit(used, limit)}${window ? `, ${window}` : ''}`}
      >
        <div className="rail-fill" data-state={state} style={{ width: `${fraction * 100}%` }} />
      </div>

      <span className="font-mono text-sm text-foreground">{ofLimit(used, limit)}</span>

      {(window || hint) && (
        <p className="col-span-2 text-xs text-muted-foreground sm:col-start-2">
          {[window, hint].filter(Boolean).join(' · ')}
        </p>
      )}
    </div>
  );
}

/**
 * A rail for a plain count against a ceiling — tenants against a licence, say. Same mark, no
 * byte formatting.
 */
export function CountRail({
  label, used, limit, unit, className,
}: { label: string; used: number; limit: number; unit?: string; className?: string }) {
  const fraction = limit > 0 ? Math.min(used / limit, 1) : 0;
  return (
    <div
      className={cn(
        'grid grid-cols-[1fr_auto] items-center gap-x-4 gap-y-1',
        'sm:grid-cols-[minmax(6rem,9rem)_1fr_auto]',
        className,
      )}
    >
      <span className="truncate text-sm text-foreground">{label}</span>
      <div
        className="rail order-last col-span-2 sm:order-none sm:col-span-1"
        role="meter"
        aria-label={label}
        aria-valuenow={Math.round(fraction * 100)}
      >
        <div className="rail-fill" data-state={railState(fraction)} style={{ width: `${fraction * 100}%` }} />
      </div>
      <span className="font-mono text-sm text-foreground">
        {used} / {limit}{unit ? ` ${unit}` : ''}
      </span>
    </div>
  );
}
