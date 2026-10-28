import { useQuery } from '@tanstack/react-query';
import { csrfHeader } from '@dcms/core';
import { runtimeConfig } from './runtime-config';

const adminApiBase = runtimeConfig.adminApiBase;
const STORAGE_KEY = 'dcms.tenant';

export interface TenantSummary {
  tenantId: string;
  slug: string;
  name: string;
}

/** Switches tenant with a full load, because permissions and almost every query are tenant-scoped. */
export function switchTenant(slug: string): void {
  setCurrentTenantSlug(slug);
  window.location.assign('/');
}

const environmentKey = (slug: string) => `dcms.environment:${slug}`;

/** The environment selected in the current tenant (ADR 0028); null is production. */
export function getCurrentEnvironment(): string | null {
  const slug = getCurrentTenantSlug();
  return slug ? localStorage.getItem(environmentKey(slug)) : null;
}

/**
 * Switches environment with a full load, keeping the section (`/sites/abc/settings` lands on
 * `/sites`): the same screens exist there, but the ids in the rest of the path are the other
 * environment's.
 */
export function switchEnvironment(name: string | null): void {
  const slug = getCurrentTenantSlug();
  if (!slug) return;
  if (name && name !== 'production') localStorage.setItem(environmentKey(slug), name);
  else localStorage.removeItem(environmentKey(slug));
  const section = window.location.pathname.split('/')[1];
  window.location.assign(section ? `/${section}` : '/');
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
 * <p>`X-Dcms-Tenant` carries the tenant SLUG (Finbuckle resolves the tenant by identifier), and
 * `X-Dcms-Environment` the environment's name outside production (ADR 0028), in both authentication modes — it names a tenant rather than claiming one, and
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
  const environment = getCurrentEnvironment();
  if (environment) headers['X-Dcms-Environment'] = environment;
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
