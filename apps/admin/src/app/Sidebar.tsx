import { Link, useRouterState } from '@tanstack/react-router';
import { Sparkles } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { NavLinkBody, SidebarNav, cn, type ShellNavItem } from '@dcms/ui';
import { type MyPermissions, can } from '../lib/permissions';
import { NAV, NAV_GROUPS } from './nav';

/**
 * The admin rail.
 *
 * The rendering, grouping, active rule and collapsed presentation all live in `@dcms/ui`; what
 * is left here is the two things only this app can supply — its permissions filter and its own
 * router's `<Link>`.
 */
export function Sidebar({
  me,
  collapsed,
  onToggle,
  inDrawer = false,
  onNavigate,
}: {
  me: MyPermissions | undefined;
  collapsed: boolean;
  onToggle: () => void;
  inDrawer?: boolean;
  onNavigate?: () => void;
}) {
  const { t } = useTranslation();
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  // In the drawer there is no reason to collapse: the drawer is already the narrow-screen
  // answer, and a collapsed drawer is a column of unlabelled icons over a dimmed page.
  const isCollapsed = inDrawer ? false : collapsed;

  const items: ShellNavItem[] = NAV.filter((item) =>
    item.superAdmin ? (me?.isSuperAdmin ?? false) : !item.perm || can(me, item.perm),
  ).map((item) => ({
    to: item.to,
    label: t(item.labelKey),
    icon: item.icon,
    group: item.group,
    exact: item.to === '/',
  }));

  return (
    <SidebarNav
      items={items}
      groups={NAV_GROUPS.map((g) => ({ id: g.id, label: t(g.labelKey) }))}
      pathname={pathname}
      collapsed={isCollapsed}
      onToggleCollapse={inDrawer ? undefined : onToggle}
      collapseLabel={t('nav.collapse')}
      ariaLabel={t('nav.sections')}
      onNavigate={onNavigate}
      className={cn(
        // In the drawer the sheet owns the width; the rail's own width would leave a strip of
        // sheet background down one side.
        inDrawer ? 'w-full' : cn('transition-[width] duration-200', isCollapsed ? 'w-16' : 'w-62'),
      )}
      brand={
        <>
          <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-sidebar-accent text-white">
            <Sparkles className="h-4 w-4" aria-hidden />
          </div>
          {isCollapsed ? null : <span className="truncate font-semibold">{t('app.name')}</span>}
        </>
      }
      renderLink={(item, { active, collapsed: isNarrow, className, onNavigate: close }) => (
        <Link
          key={item.to}
          to={item.to}
          aria-current={active ? 'page' : undefined}
          onClick={close}
          className={className}
        >
          {/* The active marker is a bar rather than only a background tint, so the current
              section is identifiable when the rail is collapsed to icons. */}
          {active ? (
            <span
              aria-hidden
              className="absolute left-0 top-1/2 h-5 w-1 -translate-y-1/2 rounded-r bg-sidebar-accent"
            />
          ) : null}
          <NavLinkBody item={item} collapsed={isNarrow} />
        </Link>
      )}
    />
  );
}
