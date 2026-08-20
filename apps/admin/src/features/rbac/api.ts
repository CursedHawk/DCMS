import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api';

export interface Role {
  id: string;
  name: string;
  isSystem: boolean;
  permissions: string[];
  /** How many members currently hold this role. */
  memberCount: number;
}

export interface Member {
  membershipId: string;
  userId: string;
  email: string;
  roleIds: string[];
}

/** An invitation that has not been accepted yet — still live, or lapsed. */
export interface Invitation {
  id: string;
  email: string;
  roleIds: string[];
  createdAt: string;
  expiresAt: string;
  expired: boolean;
}

/** One concrete thing a permission grants access to (a plugin instance, a site). */
export interface PermissionFeatureRef {
  id: string;
  name: string;
}

/** What a permission actually governs, for "what does granting this affect?". */
export interface PermissionFeature {
  kind: 'platform' | 'plugin' | 'site';
  id: string | null;
  name: string;
  /** Admin route this permission gates, when there is one. */
  route: string | null;
  /**
   * False for a plugin that ships in the binary but has no enabled instance in
   * this tenant: granting its permissions is harmless but grants access to nothing.
   */
  inUse: boolean;
  /** The enabled instances covered, for plugin permissions. */
  instances: PermissionFeatureRef[];
}

export interface PermissionDef {
  key: string;
  displayName: string;
  group: string;
  feature: PermissionFeature;
}

export function useRoles() {
  return useQuery({ queryKey: ['roles'], queryFn: () => api.get<Role[]>('/admin/roles') });
}

export function useMembers() {
  return useQuery({ queryKey: ['members'], queryFn: () => api.get<Member[]>('/admin/members') });
}

export function useInvitations() {
  return useQuery({
    queryKey: ['invitations'],
    queryFn: () => api.get<Invitation[]>('/admin/invitations'),
  });
}

export function usePermissionCatalog() {
  return useQuery({
    queryKey: ['permission-catalog'],
    staleTime: 5 * 60_000,
    queryFn: () => api.get<PermissionDef[]>('/admin/permissions/catalog'),
  });
}

/** Group catalog entries by their group label, preserving first-seen order. */
export function groupPermissions(catalog: PermissionDef[]): [string, PermissionDef[]][] {
  const groups = new Map<string, PermissionDef[]>();
  for (const p of catalog) {
    const list = groups.get(p.group) ?? [];
    list.push(p);
    groups.set(p.group, list);
  }
  return [...groups.entries()];
}
