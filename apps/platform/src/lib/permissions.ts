import { useQuery } from '@tanstack/react-query';
import { platformApi } from './api';

export { can } from '@dcms/core';

/** Mirrors PlatformConsolePermissions on the server. */
export const Perm = {
  OverviewRead: 'platform:overview:read',
  UsersRead: 'platform:users:read',
  UsersWrite: 'platform:users:write',
  UsersRoles: 'platform:users:roles',
  TenantsRead: 'platform:tenants:read',
  TenantsWrite: 'platform:tenants:write',
  TenantsLifecycle: 'platform:tenants:lifecycle',
  AuditRead: 'platform:audit:read',
  TracesRead: 'platform:traces:read',
  ObservabilityRead: 'platform:observability:read',
  OpsRead: 'platform:ops:read',
  OpsAct: 'platform:ops:act',
  LogsRead: 'platform:logs:read',
  LogsPurge: 'platform:logs:purge',
  RolesManage: 'platform:roles:manage',
  CertificatesManage: 'platform:certificates:manage',
} as const;

export interface PlatformMe {
  userId: string | null;
  name: string | null;
  email: string | null;
  isSuperAdmin: boolean;
  roles: string[];
  permissions: string[];
}

/**
 * Who the caller is and what they may reach.
 *
 * `retry: false` because the interesting failure is a 403 — an authenticated user who is not a
 * platform operator — and retrying it three times only delays telling them so.
 */
export function useMe(enabled: boolean) {
  return useQuery({
    queryKey: ['platform-me'],
    enabled,
    retry: false,
    staleTime: 60_000,
    queryFn: () => platformApi.get<PlatformMe>('/me'),
  });
}
