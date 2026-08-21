import {
  Bot,
  Boxes,
  Building2,
  FileJson,
  FileText,
  Globe,
  Image,
  Inbox,
  LayoutDashboard,
  type LucideIcon,
  MessagesSquare,
  PanelsTopLeft,
  Plug,
  ScrollText,
  ShieldCheck,
  TrendingUp,
  Users,
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

export const NAV: NavItem[] = [
  { to: '/', labelKey: 'nav.dashboard', icon: LayoutDashboard, group: 'main' },

  { to: '/content', labelKey: 'nav.content', icon: FileText, perm: Perm.ContentRead, group: 'build' },
  { to: '/media', labelKey: 'nav.media', icon: Image, perm: Perm.MediaRead, group: 'build' },
  { to: '/plugins', labelKey: 'nav.plugins', icon: Plug, perm: Perm.PluginsManage, group: 'build' },
  { to: '/sites', labelKey: 'nav.sites', icon: PanelsTopLeft, perm: Perm.SiteEdit, group: 'build' },
  { to: '/openapi', labelKey: 'nav.openapi', icon: FileJson, group: 'build' },

  { to: '/forms', labelKey: 'nav.forms', icon: Inbox, perm: Perm.ContentRead, group: 'main' },
  { to: '/analytics', labelKey: 'nav.analytics', icon: TrendingUp, perm: Perm.AnalyticsRead, group: 'main' },
  { to: '/chat', labelKey: 'nav.chat', icon: MessagesSquare, perm: Perm.ChatRead, group: 'main' },

  { to: '/members', labelKey: 'nav.members', icon: Users, perm: Perm.MembersManage, group: 'admin' },
  { to: '/roles', labelKey: 'nav.roles', icon: ShieldCheck, perm: Perm.RolesManage, group: 'admin' },
  { to: '/audit', labelKey: 'nav.audit', icon: ScrollText, perm: Perm.AuditRead, group: 'admin' },
  { to: '/domains', labelKey: 'nav.domains', icon: Globe, perm: Perm.DomainsManage, group: 'admin' },
  { to: '/ai', labelKey: 'nav.ai', icon: Bot, perm: Perm.AiSettings, group: 'admin' },
  { to: '/workspace', labelKey: 'nav.workspace', icon: Building2, perm: Perm.TenantSettings, group: 'admin' },
  { to: '/tenants', labelKey: 'nav.tenants', icon: Boxes, superAdmin: true, group: 'admin' },
];

export const NAV_GROUPS: { id: NavItem['group']; labelKey: string }[] = [
  { id: 'main', labelKey: 'nav.dashboard' },
  { id: 'build', labelKey: 'nav.sites' },
  { id: 'admin', labelKey: 'nav.settings' },
];
