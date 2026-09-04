import { Link, useRouterState } from '@tanstack/react-router';
import { motion } from 'framer-motion';
import { PanelLeftClose, PanelLeftOpen, Sparkles } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/admin-ui';
import { type MyPermissions, can } from '../lib/permissions';
import { NAV, NAV_GROUPS } from './nav';

export function Sidebar({
  me,
  collapsed,
  onToggle,
}: {
  me: MyPermissions | undefined;
  collapsed: boolean;
  onToggle: () => void;
}) {
  const { t } = useTranslation();
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  const visible = NAV.filter((item) => {
    if (item.superAdmin) return me?.isSuperAdmin ?? false;
    if (!item.perm) return true;
    return can(me, item.perm);
  });

  return (
    <motion.aside
      animate={{ width: collapsed ? 64 : 248 }}
      transition={{ type: 'spring', stiffness: 380, damping: 34 }}
      className="flex h-full shrink-0 flex-col bg-sidebar text-sidebar-foreground"
    >
      <div className="flex h-14 items-center gap-2 px-4">
        <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-sidebar-accent text-white">
          <Sparkles className="h-4 w-4" />
        </div>
        {!collapsed ? <span className="truncate font-semibold">{t('app.name')}</span> : null}
      </div>

      <nav className="flex-1 space-y-4 overflow-y-auto px-2 py-3">
        {NAV_GROUPS.map((group) => {
          const items = visible.filter((i) => i.group === group.id);
          if (items.length === 0) return null;
          return (
            <div key={group.id}>
              {items.map((item) => {
                const active =
                  item.to === '/' ? pathname === '/' : pathname.startsWith(item.to);
                return (
                  <Link
                    key={item.to}
                    to={item.to}
                    title={collapsed ? t(item.labelKey) : undefined}
                    className={cn(
                      'group relative flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors',
                      active
                        ? 'bg-sidebar-accent/15 text-white'
                        : 'text-sidebar-muted hover:bg-white/5 hover:text-sidebar-foreground',
                    )}
                  >
                    {active ? (
                      <motion.span
                        layoutId="nav-active"
                        className="absolute left-0 top-1/2 h-5 w-1 -translate-y-1/2 rounded-r bg-sidebar-accent"
                      />
                    ) : null}
                    <item.icon className="h-4 w-4 shrink-0" />
                    {!collapsed ? <span className="truncate">{t(item.labelKey)}</span> : null}
                  </Link>
                );
              })}
            </div>
          );
        })}
      </nav>

      <button
        type="button"
        onClick={onToggle}
        className="flex items-center gap-3 border-t border-white/10 px-4 py-3 text-sm text-sidebar-muted hover:text-sidebar-foreground"
      >
        {collapsed ? <PanelLeftOpen className="h-4 w-4" /> : <PanelLeftClose className="h-4 w-4" />}
        {!collapsed ? <span>Collapse</span> : null}
      </button>
    </motion.aside>
  );
}
