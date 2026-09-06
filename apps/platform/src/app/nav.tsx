import {
  Activity, Building2, FileClock, Gauge, HardDrive, Lock, ScrollText, ShieldCheck, Users,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { Perm } from '../lib/permissions';

export interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  /**
   * Required permission. Unlike the admin SPA's nav there is no `superAdmin?: boolean` escape
   * hatch: every item here names a key, because the whole point of the platform permission
   * model is that opening an area to a role is a row insert rather than a code change. A
   * SuperAdmin still sees everything — the handler short-circuits on the role.
   */
  perm: string;
  group: 'platform' | 'operations';
}

export const NAV: NavItem[] = [
  { to: '/', label: 'Overview', icon: Gauge, perm: Perm.OverviewRead, group: 'platform' },
  { to: '/tenants', label: 'Tenants', icon: Building2, perm: Perm.TenantsRead, group: 'platform' },
  { to: '/users', label: 'Users', icon: Users, perm: Perm.UsersRead, group: 'platform' },
  { to: '/audit', label: 'Audit log', icon: FileClock, perm: Perm.AuditRead, group: 'platform' },

  { to: '/monitoring', label: 'Monitoring', icon: Activity, perm: Perm.ObservabilityRead, group: 'operations' },
  { to: '/storage', label: 'Storage', icon: HardDrive, perm: Perm.LogsRead, group: 'operations' },
  { to: '/access', label: 'Access', icon: ShieldCheck, perm: Perm.RolesManage, group: 'operations' },
  { to: '/certificates', label: 'Certificates', icon: Lock, perm: Perm.CertificatesManage, group: 'operations' },
];

export const NAV_GROUPS: { id: NavItem['group']; label: string }[] = [
  { id: 'platform', label: 'Platform' },
  { id: 'operations', label: 'Operations' },
];

export const NAV_ICON_FALLBACK = ScrollText;
