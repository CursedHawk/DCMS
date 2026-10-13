import {
  Boxes,
  FileText,
  Image,
  LayoutDashboard,
  type LucideIcon,
  PanelsTopLeft,
  Sparkles,
  Plug,
  Settings,
  Store,
} from 'lucide-react';
import { Perm } from '../lib/permissions';

export interface NavItem {
  to: string;
  labelKey: string;
  icon: LucideIcon;
  /** Required permission (omitted = always visible to authenticated users). */
  perm?: string;
  /** Only the platform SuperAdmin sees this. */
  superAdmin?: boolean;
  group: NavGroupId;
}

/**
 * The built-in menu.
 *
 * <p>No longer the source of truth — `GET /api/admin/navigation` is, because only the server
 * knows which plugin instances a workspace has enabled. This list is what the sidebar draws
 * until that answers, which is every cold load: a sidebar that is empty for 200ms reads as a
 * broken app.</p>
 *
 * <p>It therefore has to stay in step with the server's platform list, and with
 * `routeGuards.ts`. `routeGuards.test.ts` enforces the second; the first is checked by
 * `NavigationEndpointTests` on the server side.</p>
 */
export const NAV: NavItem[] = [
  { to: '/', labelKey: 'nav.dashboard', icon: LayoutDashboard, group: 'overview' },

  /*
   * In the sidebar as well as behind ⌘J. The dock answers a question about the page you are on;
   * this is where the history lives, and history nobody can find is history nobody keeps.
   */
  { to: '/assistant', labelKey: 'nav.assistant', icon: Sparkles, perm: Perm.SiteEdit, group: 'overview' },

  { to: '/sites', labelKey: 'nav.sites', icon: PanelsTopLeft, perm: Perm.SiteEdit, group: 'manage' },
  { to: '/content', labelKey: 'nav.content', icon: FileText, perm: Perm.ContentRead, group: 'manage' },
  { to: '/media', labelKey: 'nav.media', icon: Image, perm: Perm.MediaRead, group: 'manage' },

  { to: '/plugins', labelKey: 'nav.plugins', icon: Plug, perm: Perm.PluginsManage, group: 'extend' },
  { to: '/marketplace', labelKey: 'nav.marketplace', icon: Store, perm: Perm.PluginsManage, group: 'extend' },

  /*
   * One Settings entry, not six.
   *
   * Members, Roles, Audit log, Domains, AI, Workspace and API Docs used to each have a line here,
   * which grew the sidebar by one every time the product gained a setting and put "what this
   * workspace is called" at the same level as the work people come here to do. They are now
   * sections inside `/settings` (see routeGuards.ts), which draws its own sub-navigation.
   *
   * No permission on the entry itself: the landing page forwards to the first section the caller
   * may open, and one of them — the API reference — is open to every member, so it is never a
   * link to nothing.
   */
  { to: '/settings', labelKey: 'nav.settings', icon: Settings, group: 'workspace' },
  { to: '/tenants', labelKey: 'nav.tenants', icon: Boxes, superAdmin: true, group: 'platform' },
];

/**
 * The sidebar's sections, in order.
 *
 * <p>Headings name a kind of work, not one of the destinations under them — this list used to
 * borrow "Dashboard", "Sites" and "Settings" as headings, so Content and Media read as parts of
 * Sites and every plugin's inbox as part of the Dashboard.</p>
 *
 * <p>`apps` is where plugins' screens go; the server lets a plugin choose only `apps` or
 * `manage` (NavigationEndpoints.PluginGroup), and `navGroup` below puts anything else there too,
 * so an entry can never land in a section that is not drawn. `overview` has no heading: two
 * entries at the top of a menu do not need a title.</p>
 */
export const NAV_GROUPS = [
  { id: 'overview', labelKey: null },
  { id: 'manage', labelKey: 'nav.groups.manage' },
  { id: 'apps', labelKey: 'nav.groups.apps' },
  { id: 'extend', labelKey: 'nav.groups.extend' },
  { id: 'workspace', labelKey: 'nav.groups.workspace' },
  { id: 'platform', labelKey: 'nav.groups.platform' },
] as const;

export type NavGroupId = (typeof NAV_GROUPS)[number]['id'];

/** The section an entry from the server is drawn in; one this console does not know becomes `apps`. */
export function navGroup(id: string): NavGroupId {
  return NAV_GROUPS.some((g) => g.id === id) ? (id as NavGroupId) : 'apps';
}
