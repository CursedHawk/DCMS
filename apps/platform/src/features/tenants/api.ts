import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
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
