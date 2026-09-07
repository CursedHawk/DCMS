import { createApiClient } from '@dcms/core';
import { getAccessToken, renewSilently } from '../auth';
import { runtimeConfig } from '../runtime-config';

export { ApiError } from '@dcms/core';

/**
 * Bearer only. There is deliberately no X-Dcms-Tenant header anywhere in this console: nothing
 * it serves is scoped to a tenant, and a stray ambient tenant is exactly how a cross-tenant
 * tool starts silently showing one tenant's rows under another's name.
 *
 * Where a page does need to act on one tenant — the admin-api tenancy endpoints — the tenant
 * is named in the route, not in a header.
 */
async function headers(): Promise<Record<string, string>> {
  const token = await getAccessToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/** platform-api: observability, ops, purge, platform roles. */
export const platformApi = createApiClient({
  base: runtimeConfig.platformApiBase,
  headers,
  renew: renewSilently,
});

/** identity: the user directory. It owns users, so it serves them. */
export const identityApi = createApiClient({
  base: runtimeConfig.identityApiBase,
  headers,
  renew: renewSilently,
});

/** admin-api: tenancy and the audit log, which it already owns (ADR 0003). */
export const adminApi = createApiClient({
  base: runtimeConfig.adminApiBase,
  headers,
  renew: renewSilently,
});
