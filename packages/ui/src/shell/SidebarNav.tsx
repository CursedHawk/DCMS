import { PanelLeftClose, PanelLeftOpen } from 'lucide-react';
import { cn } from '../cn';
import { Tooltip, TooltipContent, TooltipTrigger } from '../ui/tooltip';
import { type ShellNavGroup, type ShellNavItem, isActive } from './nav';

/**
 * The navigation rail, shared by both consoles.
 *
 * `renderLink` is a prop because the two apps have their own router instances and a `<Link>`
 * from one router does not work under the other's provider. Everything else — the grouping,
 * the active rule, the collapsed presentation, the keyboard affordances — is the same job in
 * both places and used to be two copies of it.
 */
export function SidebarNav({
  items,
  groups = [],
  pathname,
  collapsed = false,
  onToggleCollapse,
  collapseLabel = 'Collapse',
  brand,
  renderLink,
  onNavigate,
  className,
  ariaLabel = 'Sections',
}: {
  items: readonly ShellNavItem[];
  groups?: readonly ShellNavGroup[];
  pathname: string;
  /** Icons-only. The toggle is hidden entirely when `onToggleCollapse` is omitted. */
  collapsed?: boolean;
  onToggleCollapse?: () => void;
  collapseLabel?: string;
  brand?: React.ReactNode;
  renderLink: (item: ShellNavItem, props: LinkRenderProps) => React.ReactNode;
  /** Called after any item is chosen — the drawer uses it to close itself. */
  onNavigate?: () => void;
  className?: string;
  ariaLabel?: string;
}) {
  const grouped = [
    { id: '__ungrouped', label: undefined, items: items.filter((i) => !i.group) },
    ...groups.map((g) => ({ ...g, items: items.filter((i) => i.group === g.id) })),
  ].filter((g) => g.items.length > 0);

  return (
    <div className={cn('flex h-full min-h-0 flex-col bg-sidebar text-sidebar-foreground', className)}>
      {brand ? <div className="flex h-14 shrink-0 items-center gap-2 px-4">{brand}</div> : null}

      <nav aria-label={ariaLabel} className="min-h-0 flex-1 space-y-4 overflow-y-auto px-2 py-3">
        {grouped.map((group) => (
          <div key={group.id}>
            {group.label && !collapsed ? (
              <h2 className="px-3 pb-1 text-xs font-medium text-sidebar-muted">{group.label}</h2>
            ) : null}
            {group.items.map((item) => {
              const active = isActive(item, pathname);
              const body = renderLink(item, {
                active,
                collapsed,
                onNavigate,
                className: cn(
                  'group relative flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors',
                  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sidebar-accent',
                  /*
                   * The active item is the sidebar's own foreground, not its accent.
                   *
                   * Brand indigo on a 15%-indigo tint over the dark rail measures 2.58:1 — below
                   * the 4.5:1 minimum, and on the one item somebody is most likely to be looking
                   * for. axe caught it on every page. The accent still marks the item, through
                   * the tint and the bar the app draws beside it; what changed is that the label
                   * is now readable.
                   */
                  active
                    ? 'bg-sidebar-accent/15 text-sidebar-foreground'
                    : 'text-sidebar-muted hover:bg-white/5 hover:text-sidebar-foreground',
                  collapsed && 'justify-center px-0',
                ),
              });

              // Collapsed to icons, the label has to come back as a tooltip or the rail is
              // a column of glyphs with no names.
              return collapsed ? (
                <Tooltip key={item.to}>
                  <TooltipTrigger asChild>{body as React.ReactElement}</TooltipTrigger>
                  <TooltipContent side="right">{item.label}</TooltipContent>
                </Tooltip>
              ) : (
                body
              );
            })}
          </div>
        ))}
      </nav>

      {onToggleCollapse ? (
        <button
          type="button"
          onClick={onToggleCollapse}
          aria-label={collapseLabel}
          aria-pressed={collapsed}
          className={cn(
            'flex shrink-0 items-center gap-3 border-t border-white/10 px-4 py-3 text-sm text-sidebar-muted',
            'hover:text-sidebar-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-sidebar-accent',
            collapsed && 'justify-center px-0',
          )}
        >
          {collapsed ? (
            <PanelLeftOpen className="h-4 w-4" aria-hidden />
          ) : (
            <PanelLeftClose className="h-4 w-4" aria-hidden />
          )}
          {collapsed ? null : <span>{collapseLabel}</span>}
        </button>
      ) : null}
    </div>
  );
}

export interface LinkRenderProps {
  active: boolean;
  collapsed: boolean;
  className: string;
  onNavigate?: () => void;
}

/** The inside of a nav link: icon, label, optional count. Apps wrap this in their own `<Link>`. */
export function NavLinkBody({ item, collapsed }: { item: ShellNavItem; collapsed: boolean }) {
  return (
    <>
      <item.icon className="h-4 w-4 shrink-0" aria-hidden />
      {collapsed ? (
        <span className="sr-only">{item.label}</span>
      ) : (
        <>
          <span className="truncate">{item.label}</span>
          {item.badge ? (
            // Same reason as the active label: accent-on-accent-tint does not meet contrast on
            // the dark rail, and a count nobody can read is not a count.
            <span className="ml-auto rounded-full bg-sidebar-accent px-1.5 text-[11px] font-semibold text-white">
              {item.badge > 99 ? '99+' : item.badge}
            </span>
          ) : null}
        </>
      )}
    </>
  );
}
