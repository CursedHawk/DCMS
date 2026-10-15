import type { ReactNode } from 'react';
import { cn } from '../cn';

/**
 * Circular progress. `value` is 0–1 (clamped), or `null` for work whose size is not known yet,
 * which draws a turning arc instead of a fill. `children` sit in the middle — a count, an icon.
 */
export function ProgressRing({
  value,
  size = 22,
  stroke = 2.5,
  label,
  className,
  children,
}: {
  value: number | null;
  size?: number;
  stroke?: number;
  label?: string;
  className?: string;
  children?: ReactNode;
}) {
  const r = (size - stroke) / 2;
  const circumference = 2 * Math.PI * r;
  const pct = value === null ? null : Math.round(Math.min(1, Math.max(0, value)) * 100);
  // Indeterminate: a quarter arc that turns. Determinate: the arc is the fraction done.
  const filled = pct === null ? circumference / 4 : (circumference * pct) / 100;

  return (
    <span
      role="progressbar"
      aria-label={label}
      aria-valuemin={pct === null ? undefined : 0}
      aria-valuemax={pct === null ? undefined : 100}
      aria-valuenow={pct ?? undefined}
      className={cn('relative inline-flex items-center justify-center', className)}
      style={{ width: size, height: size }}
    >
      <svg
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
        aria-hidden
        className={cn('-rotate-90', pct === null && 'motion-safe:animate-spin')}
      >
        <circle cx={size / 2} cy={size / 2} r={r} fill="none" strokeWidth={stroke} className="stroke-muted" />
        {/* At 0 a round cap would still paint a dot, so there is no arc at all. */}
        {filled > 0 ? (
          <circle
            cx={size / 2}
            cy={size / 2}
            r={r}
            fill="none"
            strokeWidth={stroke}
            strokeLinecap="round"
            strokeDasharray={`${filled} ${circumference}`}
            className="stroke-primary transition-[stroke-dasharray] duration-300 ease-out"
          />
        ) : null}
      </svg>
      {children !== undefined ? (
        <span className="absolute inset-0 flex items-center justify-center text-[9px] font-semibold leading-none">
          {children}
        </span>
      ) : null}
    </span>
  );
}
