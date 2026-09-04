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

/**
 * Adopts `?tenant=<slug>` from the URL, once, before anything reads the selection.
 *
 * <p>The platform console links here to manage one tenant's media or content — it has no
 * media browser of its own and rebuilding one against a cross-tenant API would be a second
 * implementation of a screen that already exists. Without this, those links open whichever
 * tenant this browser last had selected, which is worse than not linking at all: the page
 * looks right and is showing somebody else's workspace.</p>
 *
 * <p>Called from main.tsx before the app renders, so the first request already carries the
 * right header. The parameter is then stripped from the URL, because a tenant pinned in the
 * address bar would silently override the switcher on every later reload.</p>
 *
 * <p>Nothing is trusted here beyond a string: the slug becomes the X-Dcms-Tenant header, which
 * is a request rather than a claim — the server resolves it and TenantMembershipMiddleware
 * refuses any caller who is not a member. A hand-typed slug grants nothing.</p>
 */
export function adoptTenantFromUrl(): void {
  const url = new URL(window.location.href);
  const slug = url.searchParams.get('tenant');
  if (!slug) return;

  setCurrentTenantSlug(slug);
  url.searchParams.delete('tenant');
  window.history.replaceState({}, '', url.toString());
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
