import { useQuery } from '@tanstack/react-query';
import { getAccessToken } from './auth';
import { runtimeConfig } from './runtime-config';

const adminApiBase = runtimeConfig.adminApiBase;
const STORAGE_KEY = 'dcms.tenant';

export interface TenantSummary {
  tenantId: string;
  slug: string;
  name: string;
}

/** The selected tenant SLUG (used as the X-Dcms-Tenant header). */
export function getCurrentTenantSlug(): string | null {
  return localStorage.getItem(STORAGE_KEY);
}

export function setCurrentTenantSlug(slug: string | null): void {
  if (slug) {
    localStorage.setItem(STORAGE_KEY, slug);
  } else {
    localStorage.removeItem(STORAGE_KEY);
  }
}

/**
 * Authorization + tenant headers for admin-api calls. The tenant header carries
 * the tenant SLUG (Finbuckle resolves the tenant by identifier).
 */
export async function adminHeaders(): Promise<Record<string, string>> {
  const headers: Record<string, string> = {};
  const token = await getAccessToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  const slug = getCurrentTenantSlug();
  if (slug) headers['X-Dcms-Tenant'] = slug;
  return headers;
}

export function useMyTenants(enabled: boolean) {
  return useQuery({
    queryKey: ['me-tenants'],
    enabled,
    queryFn: async (): Promise<TenantSummary[]> => {
      const token = await getAccessToken();
      const res = await fetch(`${adminApiBase}/admin/me/tenants`, {
        headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      });
      if (!res.ok) throw new Error(`me/tenants failed: ${res.status}`);
      return res.json();
    },
  });
}
