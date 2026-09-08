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

/*
 * There is deliberately no admin-api client here any more.
 *
 * The console used to call three origins. It now calls two: platform-api for everything the
 * console does, and identity for the user directory. Certificates, the platform bell, tenant
 * lifecycle and the analytics prune are still PERFORMED by admin-api — it owns those schemas —
 * but the console asks platform-api, which checks the operator's platform permission and
 * forwards on a service token. That is what makes those permission keys mean anything: on
 * admin-api the only check available was the SuperAdmin role.
 *
 * identity stays a direct call on purpose rather than becoming a third proxied area. Its user
 * endpoints mint SuperAdmins and are gated on the global ROLE, not on this permission table —
 * and platform:roles:manage edits this table, so proxying them would let its holder grant
 * themselves platform:users:roles and then mint one. Two clients, not one.
 */
