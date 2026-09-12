import type { LucideIcon } from 'lucide-react';
import { Button, cn } from '@dcms/ui';

/**
 * The shared vocabulary of the bottom panel.
 *
 * <p>One rule runs through all four tabs and is the reason they look alike: <b>each of them
 * distinguishes "nothing to report" from "nothing has been checked"</b>. An empty Problems list
 * with the preview switched off does not mean the code is fine, and a build history with no
 * entries does not mean nothing was built. Saying "clean" in either case would be the panel
 * lying quietly, which is worse than saying nothing.</p>
 *
 * <p>So there are two empty states, not one: {@link PanelEmpty} for "we looked and there is
 * nothing", and {@link PanelUnchecked} for "we have not looked", which always carries the action
 * that would make it look.</p>
 */

export function PanelEmpty({ title, icon: Icon }: { title: string; icon?: LucideIcon }) {
  return (
    <p className="flex h-full items-center justify-center gap-2 px-4 text-center text-xs text-muted-foreground">
      {Icon ? <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden /> : null}
      {title}
    </p>
  );
}

export function PanelUnchecked({
  title,
  description,
  action,
  onAction,
  icon: Icon,
}: {
  title: string;
  description?: string;
  action?: string;
  onAction?: () => void;
  icon?: LucideIcon;
}) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-2 px-4 text-center">
      <p className="flex items-center gap-2 text-xs font-medium text-foreground">
        {Icon ? <Icon className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden /> : null}
        {title}
      </p>
      {description ? (
        <p className="max-w-md text-[11px] text-muted-foreground">{description}</p>
      ) : null}
      {action && onAction ? (
        <Button size="sm" variant="outline" onClick={onAction}>
          {action}
        </Button>
      ) : null}
    </div>
  );
}

/**
 * A row that points at a place in the code.
 *
 * <p>Mono for the path and the line number, because those are verbatim machine text the author
 * may want to copy; the message itself is prose and set in the UI face. That split is the
 * panel's typographic rule — monospace means "this is exactly what the machine said", never
 * "this looks technical".</p>
 */
export function PanelRow({
  onClick,
  children,
  className,
}: {
  onClick?: () => void;
  children: React.ReactNode;
  className?: string;
}) {
  const classes = cn(
    'flex w-full items-baseline gap-2 rounded px-2 py-1 text-left text-xs',
    onClick && 'hover:bg-accent',
    className,
  );

  if (!onClick) return <div className={classes}>{children}</div>;
  return (
    <button type="button" onClick={onClick} className={classes}>
      {children}
    </button>
  );
}
