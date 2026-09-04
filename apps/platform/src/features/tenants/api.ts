import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { adminApi, platformApi } from '../../lib/api';

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
 * Suspend and resume go to admin-api, which owns the tenancy schema (ADR 0003) and the two
 * enforcement points that make the status mean anything. The console does not write tenancy
 * itself; it asks the owner, carrying the operator's own token.
 */
export function useSetTenantStatus() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ tenantId, suspend }: { tenantId: string; suspend: boolean }) =>
      adminApi.post<void>(`/admin/tenants/${tenantId}/${suspend ? 'suspend' : 'resume'}`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['platform-tenants'] });
      void qc.invalidateQueries({ queryKey: ['platform-overview'] });
    },
  });
}
