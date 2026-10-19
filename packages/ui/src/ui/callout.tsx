import { AlertTriangle, CheckCircle2, Info, XCircle, type LucideIcon } from 'lucide-react';
import { cn } from '../cn';

type Tone = 'info' | 'warning' | 'success' | 'destructive';

const tones: Record<Tone, { box: string; icon: LucideIcon }> = {
  info: { box: 'border-primary/30 bg-primary/5 text-foreground', icon: Info },
  warning: { box: 'border-warning/40 bg-warning/10 text-warning-foreground', icon: AlertTriangle },
  success: { box: 'border-success/40 bg-success/10 text-success-foreground', icon: CheckCircle2 },
  destructive: { box: 'border-destructive/40 bg-destructive/5 text-destructive', icon: XCircle },
};

/**
 * A boxed note inside a page or panel: "this key is missing", "publishing will overwrite".
 *
 * <p>The same amber box was copied by hand into eight places, each with its own padding and
 * shade of amber. `compact` is the size the editor rails used (`p-2 text-xs`).</p>
 */
export function Callout({
  tone = 'info',
  title,
  children,
  compact,
  className,
  icon = true,
}: {
  tone?: Tone;
  title?: React.ReactNode;
  children?: React.ReactNode;
  compact?: boolean;
  className?: string;
  /** Off for a callout too small to spare the space, or one that brings its own. */
  icon?: boolean;
}) {
  const { box, icon: Icon } = tones[tone];
  return (
    // No role="alert": these are rendered with the page, not raised by an action, and an alert
    // role makes a screen reader interrupt with every one on every visit.
    <div
      className={cn(
        'flex gap-2 rounded-md border [overflow-wrap:anywhere]',
        compact ? 'p-2 text-xs' : 'p-3 text-sm',
        box,
        className,
      )}
    >
      {icon ? (
        <Icon
          className={cn('shrink-0', compact ? 'mt-px h-3.5 w-3.5' : 'mt-0.5 h-4 w-4')}
          aria-hidden
        />
      ) : null}
      <div className="min-w-0 flex-1 space-y-1">
        {title ? <p className="font-medium">{title}</p> : null}
        {children}
      </div>
    </div>
  );
}
