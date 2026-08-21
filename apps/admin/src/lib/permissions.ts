import { useQuery } from '@tanstack/react-query';
import { api } from './api';
import { getCurrentTenantSlug } from '../tenants';

export interface MyPermissions {
  isSuperAdmin: boolean;
  permissions: string[];
}

/** Platform permission keys (mirrors PlatformPermissions on the server). */
export const Perm = {
  TenantSettings: 'tenant:settings',
  MembersManage: 'members:manage',
  RolesManage: 'roles:manage',
  DomainsManage: 'domains:manage',
  PluginsManage: 'plugins:manage',
  MediaRead: 'media:read',
  MediaWrite: 'media:write',
  SiteEdit: 'site:edit',
  SitePublish: 'site:publish',
  AiSettings: 'ai:settings',
  AnalyticsRead: 'analytics:read',
  ContentRead: 'content:read',
  ContentWrite: 'content:write',
  ContentPublish: 'content:publish',
  ChatRead: 'chat:read',
  ChatManage: 'chat:manage',
  AuditRead: 'audit:read',
  AuditExport: 'audit:export',
} as const;

/** Per-site git repo permission keys (resource-scoped; mirrors PlatformPermissions). */
export const repoRead = (siteId: string) => `repo:${siteId}:read`;
export const repoWrite = (siteId: string) => `repo:${siteId}:write`;

export function useMyPermissions(enabled: boolean) {
  // Re-fetch when the tenant changes (permissions are tenant-scoped).
  const tenant = getCurrentTenantSlug();
  return useQuery({
    queryKey: ['me-permissions', tenant],
    enabled,
    staleTime: 60_000,
    queryFn: () => api.get<MyPermissions>('/admin/me/permissions'),
  });
}

/** True if the user holds the permission (SuperAdmin holds everything). */
export function can(me: MyPermissions | undefined, permission: string): boolean {
  if (!me) return false;
  return me.isSuperAdmin || me.permissions.includes(permission);
}
