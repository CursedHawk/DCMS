import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UsageSnapshot } from '@dcms/ui';
import { platformApi } from '../../lib/api';

export interface TenantRow {
  tenantId: string;
  slug: string;
  name: string | null;
  status: string;
  createdAt: string | null;
  members: number;
  domains: number;
  verifiedDomains: number;
  sites: number;
  contentItems: number;
  publishedItems: number;
  mediaAssets: number;
  enabledPlugins: number;
  visitorAccounts: number;
  formSubmissions: number;
  storageBytes: number;
  /** The cap storageBytes is held to; uploads are refused once it is reached. */
  quotaBytes: number;
  /** Everything the tenant stores, from its latest usage snapshot; 0 before the first one. */
  usageBytes: number;
}

/** One tenant for its page: the directory row, the media split and the usage snapshot. */
export interface TenantDetail {
  tenant: TenantRow;
  originalBytes: number;
  variantBytes: number;
  /** Null until the tenant has been measured once. */
  usage: UsageSnapshot | null;
}

/** Who and what a tenant has. From admin-api, because the obs views carry no email address. */
export interface TenantDirectory {
  domains: { hostname: string; verified: boolean; primary: boolean; createdAt: string }[];
  members: { userId: string; email: string; createdAt: string; roles: string[] }[];
  plugins: { id: string; pluginId: string; name: string; slug: string; enabled: boolean; createdAt: string }[];
}

export function useTenants(search: string) {
  return useQuery({
    queryKey: ['platform-tenants', search],
    queryFn: () =>
      platformApi.get<TenantRow[]>(
        `/tenants${search ? `?search=${encodeURIComponent(search)}` : ''}`,
      ),
  });
}

/** Under `platform-tenants`, so the live `tenants` tag and every tenant mutation refresh it too. */
export function useTenant(tenantId: string) {
  return useQuery({
    queryKey: ['platform-tenants', 'detail', tenantId],
    queryFn: () => platformApi.get<TenantDetail>(`/tenants/${tenantId}`),
  });
}

export function useTenantDirectory(tenantId: string) {
  return useQuery({
    queryKey: ['platform-tenants', 'directory', tenantId],
    queryFn: () => platformApi.get<TenantDirectory>(`/tenants/${tenantId}/directory`),
  });
}

/** Measures the tenant now (admin-api, at most every two minutes); the page then re-reads obs. */
export function useRecalculateUsage(tenantId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => platformApi.post<unknown>(`/tenants/${tenantId}/usage/recalculate`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['platform-tenants'] }),
  });
}

/**
 * Suspend and resume are performed by admin-api, which owns the tenancy schema (ADR 0003) and
 * the two enforcement points that make the status mean anything. platform-api checks the
 * operator against platform:tenants:lifecycle and forwards, naming them in the propagated
 * actor headers so the audit record says who asked rather than which service relayed it.
 */
export function useSetTenantStatus() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ tenantId, suspend }: { tenantId: string; suspend: boolean }) =>
      platformApi.post<void>(`/tenants/${tenantId}/${suspend ? 'suspend' : 'resume'}`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['platform-tenants'] });
      void qc.invalidateQueries({ queryKey: ['platform-overview'] });
    },
  });
}

/** The tenant's storage cap. Performed by admin-api, checked here against platform:tenants:write. */
export function useSetStorageQuota() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ tenantId, quotaBytes }: { tenantId: string; quotaBytes: number }) =>
      platformApi.put<void>(`/tenants/${tenantId}/storage-quota`, { quotaBytes }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['platform-tenants'] }),
  });
}
