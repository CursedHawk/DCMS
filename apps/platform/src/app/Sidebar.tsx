import { Link, useRouterState } from '@tanstack/react-router';
import { cn } from '@dcms/ui';
import { can, type PlatformMe } from '../lib/permissions';
import { NAV, NAV_GROUPS } from './nav';

/**
 * Fixed-width and always expanded on a desktop; a horizontal strip on a narrow screen.
 *
 * <p>The admin SPA's sidebar collapses to icons because it has twenty destinations and its
 * users live in one page for an hour. This console has six, and its users arrive knowing which
 * one they want — a collapse control would be a preference to remember and a hit target to
 * miss, for 180px on a screen that is not short of them.</p>
 *
 * <p>The right border is not decoration. In dark mode the sidebar and the page ground are
 * close enough in value that without it the two surfaces merge and the nav loses its edge.</p>
 */
export function Sidebar({ me }: { me: PlatformMe | undefined }) {
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const visible = NAV.filter((item) => can(me, item.perm));

  return (
    <nav
      aria-label="Platform sections"
      className={cn(
        'shrink-0 bg-sidebar text-sidebar-foreground',
        // Narrow: a scrollable strip across the top. Wide: the full rail.
        'flex flex-row items-center gap-1 overflow-x-auto border-b border-black/30 px-3 py-2',
        'md:w-60 md:flex-col md:items-stretch md:gap-6 md:overflow-visible md:border-b-0 md:border-r md:px-3 md:py-4',
      )}
    >
      <div className="shrink-0 px-2 md:shrink">
        <span className="font-mono text-[0.8125rem] tracking-tight text-sidebar-accent">dcms</span>
        <h1 className="text-base font-semibold leading-tight">Platform</h1>
      </div>

      {NAV_GROUPS.map((group) => {
        const items = visible.filter((i) => i.group === group.id);
        if (items.length === 0) return null;
        return (
          <div key={group.id} className="flex shrink-0 flex-row gap-1 md:flex-col md:gap-0.5">
            {/* The group label is orientation for a rail of two sections; in the horizontal
                strip it would be a third of the width for no navigational benefit. */}
            <h2 className="hidden px-2 pb-1 text-xs text-sidebar-muted md:block">{group.label}</h2>
            {items.map((item) => {
              const active = item.to === '/' ? pathname === '/' : pathname.startsWith(item.to);
              return (
                <Link
                  key={item.to}
                  to={item.to}
                  aria-current={active ? 'page' : undefined}
                  className={cn(
                    'flex items-center gap-2.5 whitespace-nowrap rounded-md px-2 py-1.5 text-sm transition-colors',
                    'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sidebar-accent',
                    active
                      ? 'bg-sidebar-accent/15 text-sidebar-accent'
                      : 'text-sidebar-foreground/85 hover:bg-white/5 hover:text-sidebar-foreground',
                  )}
                >
                  <item.icon className="h-4 w-4 shrink-0" aria-hidden />
                  {item.label}
                </Link>
              );
            })}
          </div>
        );
      })}
    </nav>
  );
}
