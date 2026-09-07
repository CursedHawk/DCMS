import { Info } from 'lucide-react';
import { cn } from '../cn';
import { Popover, PopoverContent, PopoverTrigger } from '../ui/popover';

/**
 * The small circled "i" beside a control that needs a sentence of explanation.
 *
 * <p>A popover rather than a tooltip, deliberately. Tooltips vanish on the way to them, cannot
 * hold a link, and on a touch screen do not exist at all — and the things that need explaining
 * here are exactly the ones somebody will want to read twice and follow a link out of (what a
 * DNS TXT verification is, why an issuance attempt counts against a weekly ceiling, what the
 * hash chain in the audit log proves).</p>
 *
 * <p><b>It must never be placed inside a Radix <code>*Trigger asChild</code>.</b> Two `asChild`
 * Slots competing for one child do not compose — the outer one wins and the inner one's
 * handlers are dropped, silently. That is how the theme switch shipped broken once: the
 * dropdown trigger was fine, the hint swallowed it, and nothing errored. Where a control needs
 * both a hint and a menu, put the hint <em>beside</em> it, not around it.</p>
 */
export function InfoHint({
  title,
  children,
  label = 'More information',
  className,
  side = 'top',
}: {
  /** Optional heading. Omit for a single sentence; use it when the body has structure. */
  title?: string;
  children: React.ReactNode;
  /** The accessible name of the button. Say what it explains, not "info". */
  label?: string;
  className?: string;
  side?: 'top' | 'right' | 'bottom' | 'left';
}) {
  return (
    <Popover>
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-label={label}
          className={cn(
            'inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full align-middle',
            'text-muted-foreground/70 transition-colors hover:text-foreground',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
            className,
          )}
        >
          <Info className="h-3.5 w-3.5" aria-hidden />
        </button>
      </PopoverTrigger>
      <PopoverContent side={side} className="w-72 text-sm">
        {title ? <p className="mb-1 font-medium">{title}</p> : null}
        <div className="text-muted-foreground [&_a]:text-primary [&_a]:underline [&_a]:underline-offset-2">
          {children}
        </div>
      </PopoverContent>
    </Popover>
  );
}

/**
 * A label with a hint attached — the shape this is used in nine times out of ten.
 *
 * Exists so the hint sits in a consistent place relative to its label, and so the two are one
 * thing to align in a form row rather than two.
 */
export function LabelWithHint({
  children,
  hint,
  hintTitle,
  hintLabel,
  className,
}: {
  children: React.ReactNode;
  hint: React.ReactNode;
  hintTitle?: string;
  hintLabel?: string;
  className?: string;
}) {
  return (
    <span className={cn('inline-flex items-center gap-1.5', className)}>
      {children}
      <InfoHint title={hintTitle} label={hintLabel}>
        {hint}
      </InfoHint>
    </span>
  );
}
