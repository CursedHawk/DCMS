import type { LucideIcon } from 'lucide-react';
import { cn } from '../cn';

/**
 * A page's own sub-navigation: Settings' sections, Content's collections.
 *
 * <p>Both used to be hand-built, differently. At `lg` and up this is the vertical list beside the
 * page; below it, the same entries become wrapping chips above the page. The vertical list on a
 * phone was nine rows with a description each, which put every Settings section's content
 * below the fold. Chips wrap rather than scroll: a sideways scroller hides entries nobody knows
 * to swipe for.</p>
 *
 * <p>Composition rather than a data prop, because each app renders the entries with its own
 * router's `<Link>` (or a button), exactly as `SidebarNav` does — use `subNavItemClass` on it
 * and `SubNavItemBody` inside it.</p>
 */
export function SubNav({
  label,
  heading,
  children,
  className,
}: {
  /** The landmark's accessible name. */
  label: string;
  /** Shown above the list at `lg` and up; the chips need no title. */
  heading?: string;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <nav aria-label={label} className={cn('min-w-0 lg:w-60 lg:shrink-0', className)}>
      {heading ? (
        <p className="hidden px-3 pb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground lg:block">
          {heading}
        </p>
      ) : null}
      <div className="flex flex-wrap gap-2 lg:block lg:space-y-4">{children}</div>
    </nav>
  );
}

/** A labelled run of entries — one plugin instance's collections. */
export function SubNavGroup({ label, children }: { label?: string; children: React.ReactNode }) {
  return (
    <div className="contents lg:block">
      {label ? (
        <p className="basis-full truncate px-1 text-xs font-medium text-muted-foreground lg:px-3 lg:pb-1">
          {label}
        </p>
      ) : null}
      <ul className="contents lg:block lg:space-y-0.5">{children}</ul>
    </div>
  );
}

/** For the entry's `<a>`/`<button>`, wrapped in an `<li>`. */
export function subNavItemClass(active: boolean): string {
  return cn(
    'flex max-w-full items-center gap-2 text-left text-sm transition-colors',
    // Chip below lg, row at lg.
    'rounded-full border px-3 py-1 lg:w-full lg:rounded-md lg:border-0 lg:px-3 lg:py-2',
    active
      ? 'border-transparent bg-accent font-medium text-accent-foreground'
      : 'text-muted-foreground hover:bg-muted/60 hover:text-foreground',
  );
}

export function SubNavItemBody({
  label,
  description,
  icon: Icon,
  count,
  active,
}: {
  label: string;
  /** A line under the label at `lg`; chips leave it out. */
  description?: string;
  icon?: LucideIcon;
  count?: number;
  active: boolean;
}) {
  return (
    <>
      {Icon ? (
        <Icon
          className={cn('h-4 w-4 shrink-0', active ? '' : 'text-muted-foreground')}
          aria-hidden
        />
      ) : null}
      <span className="min-w-0 flex-1">
        <span className="block truncate">{label}</span>
        {/*
          The description follows the row it is in. `text-muted-foreground` over the tinted
          active background measures 4.17:1 — under the 4.5:1 minimum, and axe caught it on the
          one row somebody is currently reading.
        */}
        {description ? (
          <span
            className={cn(
              'mt-0.5 hidden text-xs font-normal lg:block',
              active ? 'text-accent-foreground/80' : 'text-muted-foreground',
            )}
          >
            {description}
          </span>
        ) : null}
      </span>
      {count !== undefined ? (
        <span
          className={cn(
            'shrink-0 text-xs tabular-nums',
            active ? 'text-accent-foreground/70' : 'text-muted-foreground',
          )}
        >
          {count}
        </span>
      ) : null}
    </>
  );
}
