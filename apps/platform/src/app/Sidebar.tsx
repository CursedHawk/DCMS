import { Link, useRouterState } from '@tanstack/react-router';
import { NavLinkBody, SidebarNav, type ShellNavItem } from '@dcms/ui';
import { can, type PlatformMe } from '../lib/permissions';
import { NAV, NAV_GROUPS } from './nav';

/**
 * The console rail. Fixed width, never collapsible.
 *
 * <p>The admin SPA's rail collapses to icons because it has twenty destinations and its users
 * live in one page for an hour. This console has eight, and its users arrive knowing which one
 * they want — a collapse control would be a preference to remember and a hit target to miss,
 * for 180px on a screen that is not short of them. Below `lg` it becomes the frame's drawer,
 * which is a different answer to a different problem.</p>
 *
 * <p>The right border is not decoration: in dark mode the sidebar and the page ground are
 * close enough in value that without it the two surfaces merge and the nav loses its edge.</p>
 */
export function Sidebar({
  me,
  inDrawer = false,
  onNavigate,
}: {
  me: PlatformMe | undefined;
  inDrawer?: boolean;
  onNavigate?: () => void;
}) {
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  const items: ShellNavItem[] = NAV.filter((item) => can(me, item.perm)).map((item) => ({
    to: item.to,
    label: item.label,
    icon: item.icon,
    group: item.group,
    exact: item.to === '/',
  }));

  return (
    <SidebarNav
      items={items}
      groups={NAV_GROUPS}
      pathname={pathname}
      ariaLabel="Platform sections"
      onNavigate={onNavigate}
      className={inDrawer ? 'w-full' : 'w-60 border-r border-black/30'}
      brand={
        <div>
          <span className="font-mono text-[0.8125rem] tracking-tight text-sidebar-accent">dcms</span>
          <h1 className="text-base font-semibold leading-tight">Platform</h1>
        </div>
      }
      renderLink={(item, { active, collapsed, className, onNavigate: close }) => (
        <Link
          key={item.to}
          to={item.to}
          aria-current={active ? 'page' : undefined}
          onClick={close}
          className={className}
        >
          <NavLinkBody item={item} collapsed={collapsed} />
        </Link>
      )}
    />
  );
}
