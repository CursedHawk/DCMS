import { useQuery } from '@tanstack/react-query';
import { csrfHeader } from '@dcms/core';
import { runtimeConfig } from './runtime-config';

const adminApiBase = runtimeConfig.adminApiBase;
const STORAGE_KEY = 'dcms.tenant';

export interface TenantSummary {
  tenantId: string;
  slug: string;
  name: string;
  /** The workspace root's id when the workspace has environments (ADR 0026), else null. */
  workspaceId?: string | null;
  /** The environment's name; `production` on the root. Null while environments are off. */
  environment?: string | null;
  environmentOrder?: number;
}

/** What groups a tenant with its environments: the workspace root's id, or its own. */
export const workspaceOf = (t: TenantSummary): string => t.workspaceId ?? t.tenantId;

/**
 * Switches tenant with a full load, because permissions and almost every query are tenant-scoped.
 *
 * <p>Between environments of one workspace the section is kept (`/sites/abc/settings` lands on
 * `/sites`): the same screens exist there, but the ids in the rest of the path are the other
 * environment's. Anywhere else it is home, since the other workspace may have nothing alike.</p>
 */
export function switchTenant(slug: string, keepSection = false): void {
  setCurrentTenantSlug(slug);
  const section = window.location.pathname.split('/')[1];
  window.location.assign(keepSection && section ? `/${section}` : '/');
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
 * The ambient headers every admin-api call carries.
 *
 * <p>`X-Dcms-Tenant` carries the tenant SLUG (Finbuckle resolves the tenant by identifier),
 * in both authentication modes — it names a tenant rather than claiming one, and
 * TenantMembershipMiddleware still refuses a caller who is not a member.</p>
 *
 * <p>No Authorization header, and that absence is the point: it is what tells the edge to
 * attach the session's own (ADR 0014). What goes in its place on a write is the CSRF token.
 * Every hand-rolled fetch in this app builds its headers from here — chat, the IDE agent
 * client, the preview proxy — so this is the one place either had to be said.</p>
 */
export async function adminHeaders(): Promise<Record<string, string>> {
  const headers: Record<string, string> = { ...csrfHeader() };
  const slug = getCurrentTenantSlug();
  if (slug) headers['X-Dcms-Tenant'] = slug;
  return headers;
}

export function useMyTenants(enabled: boolean) {
  return useQuery({
    queryKey: ['me-tenants'],
    enabled,
    queryFn: async (): Promise<TenantSummary[]> => {
      const res = await fetch(`${adminApiBase}/admin/me/tenants`);
      if (!res.ok) throw new Error(`me/tenants failed: ${res.status}`);
      return res.json();
    },
  });
}
