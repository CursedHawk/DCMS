import { useQuery } from '@tanstack/react-query';
import { type MyPermissions } from '@dcms/core';
import { api } from './api';
import { getCurrentTenantSlug } from '../tenants';

// `can` and the MyPermissions shape are shared with the platform SPA; the keys below are
// tenant-scoped and belong to this app.
export { can, type MyPermissions } from '@dcms/core';

/** Tenant permission keys (mirrors PlatformPermissions on the server). */
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
  AiChatsReadAll: 'ai:chats:read-all',
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
