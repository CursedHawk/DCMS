import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api';

export interface Role {
  id: string;
  name: string;
  isSystem: boolean;
  permissions: string[];
}

export interface Member {
  membershipId: string;
  userId: string;
  email: string;
  roleIds: string[];
}

export interface PermissionDef {
  key: string;
  displayName: string;
  group: string;
}

export function useRoles() {
  return useQuery({ queryKey: ['roles'], queryFn: () => api.get<Role[]>('/admin/roles') });
}

export function useMembers() {
  return useQuery({ queryKey: ['members'], queryFn: () => api.get<Member[]>('/admin/members') });
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
