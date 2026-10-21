import { useNavigate, useRouterState } from '@tanstack/react-router';
import { Command } from 'cmdk';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Bell, Loader, PanelsTopLeft, Plug, Settings, UserCog, type LucideIcon } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { Dialog, DialogContent, usePermissions } from '@dcms/ui';
import { api } from '../lib/api';
import { Perm, can, type MyPermissions } from '../lib/permissions';
import { usePluginInstances } from '../features/plugins/api';
import { getCurrentTenantSlug } from '../tenants';
import { NAV, navGroup } from './nav';
import { iconByName, navLabel, useNavigation, type NavResponseItem } from './navApi';
import { SETTINGS_SECTIONS } from './routeGuards';

export interface PaletteItem {
  to: string;
  label: string;
  icon: LucideIcon;
  group: 'pages' | 'apps' | 'sites' | 'plugins' | 'settings';
}

/** Things with names, as well as pages: a site, a plugin instance. From the lists' own caches. */
export interface PaletteRecords {
  sites?: readonly { id: string; name: string }[];
  instances?: readonly { slug: string; name: string }[];
}

/**
 * Every destination this member may open, in the palette's three groups.
 *
 * <p>The nav, plus every Settings section: collapsing Members, Roles, Domains and the rest into
 * one Settings entry made the sidebar shorter and made them one keystroke further away — and
 * this palette is precisely how people reach a page whose name they know. Listing only
 * "Settings" would mean typing "domains" found nothing.</p>
 *
 * <p>Entries the server sent with their own name — a plugin's screen, an app's records, a
 * collection someone named "Press room" — come from the server's menu, because only it knows
 * which plugins this workspace has switched on. Plugins' screens are Apps; the rest are Pages.</p>
 */
export function paletteItems(
  me: MyPermissions | undefined,
  server: readonly NavResponseItem[] | undefined,
  t: (key: string) => string,
  language: string,
  records: PaletteRecords = {},
): PaletteItem[] {
  const items: PaletteItem[] = [
    ...NAV.filter((i) => (i.superAdmin ? me?.isSuperAdmin : !i.perm || can(me, i.perm))).map(
      (i): PaletteItem => ({ to: i.to, label: t(i.labelKey), icon: i.icon, group: 'pages' }),
    ),
    ...(server ?? [])
      .filter((i) => i.label !== null)
      .flatMap((i): PaletteItem[] => {
        const app = navLabel(i, language, t);
        const group = navGroup(i.group) === 'apps' ? 'apps' : 'pages';
        return [
          { to: i.to, label: app, icon: iconByName(i.icon), group },
          // "Records" alone matches every app that has records; with the app's name it is one.
          ...(i.children ?? []).map((c): PaletteItem => ({
            to: c.to,
            label: `${navLabel(c, language, t)} · ${app}`,
            icon: iconByName(i.icon),
            group,
          })),
        ];
      }),
    // Reached from the topbar rather than the menu, so they were not in the palette either.
    { to: '/notifications', label: t('nav.notifications'), icon: Bell, group: 'pages' },
    { to: '/jobs', label: t('nav.jobs'), icon: Loader, group: 'pages' },
    { to: '/account', label: t('nav.account'), icon: UserCog, group: 'pages' },
    /*
     * A site by its name. "Open the Acme site" was the sidebar, then the Sites grid, then the
     * card — the palette knew the page called Sites and nothing on it.
     */
    ...(records.sites ?? []).map(
      (s): PaletteItem => ({ to: `/sites/${s.id}`, label: s.name, icon: PanelsTopLeft, group: 'sites' }),
    ),
    ...(records.instances ?? []).map(
      (i): PaletteItem => ({ to: `/apps/${i.slug}`, label: i.name, icon: Plug, group: 'plugins' }),
    ),
    ...SETTINGS_SECTIONS.filter((s) => !s.perm || can(me, s.perm)).map(
      (s): PaletteItem => ({ to: s.to, label: t(s.labelKey), icon: Settings, group: 'settings' }),
    ),
  ];
  // One entry per destination: a route listed twice is two identical rows that both light up.
  const seen = new Set<string>();
  return items.filter((i) => !seen.has(i.to) && seen.add(i.to));
}

const RECENT_MAX = 5;
const recentKey = (tenant: string | null) => `dcms.palette.recent:${tenant ?? ''}`;

export function readRecent(tenant: string | null): string[] {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(recentKey(tenant)) ?? '[]');
    return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [];
  } catch {
    return [];
  }
}

export function rememberVisit(tenant: string | null, to: string): void {
  const next = [to, ...readRecent(tenant).filter((p) => p !== to)].slice(0, RECENT_MAX);
  try {
    localStorage.setItem(recentKey(tenant), JSON.stringify(next));
  } catch {
    // Storage full or disabled: the palette simply has no recent list.
  }
}

export function CommandPalette({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
}) {
  const { t, i18n } = useTranslation();
  const me = usePermissions();
  const navigate = useNavigate();
  // Not gated on `open`: the sidebar holds the same query, and the visit tracking below needs
  // to know the destinations while the palette is closed.
  const nav = useNavigation(!!me);
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const tenant = getCurrentTenantSlug();
  const [search, setSearch] = useState('');

  // Global ⌘K / Ctrl+K shortcut.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key.toLowerCase() === 'k' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        onOpenChange(!open);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onOpenChange]);

  // Only while open, and the same keys as the Sites and Plugins pages: usually already cached.
  const sites = useQuery({
    queryKey: ['sites'],
    enabled: open && can(me, Perm.SiteEdit),
    queryFn: () => api.get<{ id: string; name: string }[]>('/admin/sites'),
  });
  const instances = usePluginInstances(open && can(me, Perm.PluginsManage));

  const items = paletteItems(me, nav.data?.items, t, i18n.language, {
    sites: sites.data,
    instances: instances.data,
  });
  const byPath = new Map(items.map((i) => [i.to, i]));

  /*
   * Recent: the destinations this member actually went to, however they got there — the
   * sidebar, a link, this palette. Stored per workspace, as paths only, and drawn only through
   * today's list: a destination that was revoked, or a plugin that was switched off since, is
   * not offered again.
   */
  const isDestination = byPath.has(pathname);
  useEffect(() => {
    if (isDestination && pathname !== '/') rememberVisit(tenant, pathname);
  }, [pathname, tenant, isDestination]);
  const recent = open
    ? readRecent(tenant)
        .filter((p) => p !== pathname)
        .map((p) => byPath.get(p))
        .filter((i): i is PaletteItem => !!i)
    : [];

  const go = (to: string) => {
    onOpenChange(false);
    setSearch('');
    void navigate({ to: to as string });
  };

  const row = (item: PaletteItem, prefix = '') => (
    <Command.Item
      key={prefix + item.to}
      // The path makes two entries with the same label (two apps' "Records") distinct to cmdk;
      // the label is what is matched.
      value={prefix + item.to}
      keywords={[item.label]}
      onSelect={() => go(item.to)}
      className="flex cursor-pointer items-center gap-3 rounded-md px-3 py-2 text-sm aria-selected:bg-accent aria-selected:text-accent-foreground"
    >
      <item.icon className="h-4 w-4 shrink-0" aria-hidden />
      <span className="min-w-0 truncate">{item.label}</span>
    </Command.Item>
  );

  const heading =
    '[&_[cmdk-group-heading]]:px-3 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:pt-2 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:text-muted-foreground';
  const groups: { id: PaletteItem['group']; label: string }[] = [
    { id: 'pages', label: t('palette.pages') },
    { id: 'apps', label: t('nav.groups.apps') },
    { id: 'sites', label: t('nav.sites') },
    { id: 'plugins', label: t('nav.plugins') },
    { id: 'settings', label: t('nav.settings') },
  ];

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="overflow-hidden p-0" wide>
        <Command
          className="[&_[cmdk-input]]:h-12"
          // Match on the label only: the value is a path, and "settings" should not find every
          // section because their paths start with /settings.
          filter={(_value, query, keywords) =>
            (keywords ?? []).some((k) => k.toLowerCase().includes(query.trim().toLowerCase())) ? 1 : 0
          }
        >
          <Command.Input
            value={search}
            onValueChange={setSearch}
            placeholder={`${t('actions.search')}…`}
            className="w-full border-b bg-transparent px-4 text-sm outline-none placeholder:text-muted-foreground"
          />
          <Command.List className="max-h-96 overflow-y-auto p-2">
            <Command.Empty className="py-6 text-center text-sm text-muted-foreground">
              {t('common.noResults')}
            </Command.Empty>
            {/* Only before anything is typed: while searching, the same entries are found in
                their own groups, and listing them twice is noise. */}
            {search.trim() === '' && recent.length > 0 ? (
              <Command.Group heading={t('palette.recent')} className={heading}>
                {recent.map((item) => row(item, 'recent:'))}
              </Command.Group>
            ) : null}
            {groups.map((g) => {
              const members = items.filter((i) => i.group === g.id);
              return members.length > 0 ? (
                <Command.Group key={g.id} heading={g.label} className={heading}>
                  {members.map((item) => row(item))}
                </Command.Group>
              ) : null;
            })}
          </Command.List>
        </Command>
      </DialogContent>
    </Dialog>
  );
}
