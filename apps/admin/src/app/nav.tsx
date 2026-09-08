import {
  Boxes,
  FileText,
  Image,
  Inbox,
  LayoutDashboard,
  type LucideIcon,
  MessagesSquare,
  PanelsTopLeft,
  Plug,
  Settings,
  Store,
  TrendingUp,
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
  group: 'main' | 'build' | 'admin';
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
  { to: '/', labelKey: 'nav.dashboard', icon: LayoutDashboard, group: 'main' },

  { to: '/content', labelKey: 'nav.content', icon: FileText, perm: Perm.ContentRead, group: 'build' },
  { to: '/media', labelKey: 'nav.media', icon: Image, perm: Perm.MediaRead, group: 'build' },
  { to: '/plugins', labelKey: 'nav.plugins', icon: Plug, perm: Perm.PluginsManage, group: 'build' },
  { to: '/marketplace', labelKey: 'nav.marketplace', icon: Store, perm: Perm.PluginsManage, group: 'build' },
  { to: '/sites', labelKey: 'nav.sites', icon: PanelsTopLeft, perm: Perm.SiteEdit, group: 'build' },

  { to: '/forms', labelKey: 'nav.forms', icon: Inbox, perm: Perm.ContentRead, group: 'main' },
  { to: '/analytics', labelKey: 'nav.analytics', icon: TrendingUp, perm: Perm.AnalyticsRead, group: 'main' },
  { to: '/chat', labelKey: 'nav.chat', icon: MessagesSquare, perm: Perm.ChatRead, group: 'main' },

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
  { to: '/settings', labelKey: 'nav.settings', icon: Settings, group: 'admin' },
  { to: '/tenants', labelKey: 'nav.tenants', icon: Boxes, superAdmin: true, group: 'admin' },
];

export const NAV_GROUPS: { id: NavItem['group']; labelKey: string }[] = [
  { id: 'main', labelKey: 'nav.dashboard' },
  { id: 'build', labelKey: 'nav.sites' },
  { id: 'admin', labelKey: 'nav.settings' },
];
