import { createApiClient } from '@dcms/core';
import { renewSilently } from '../auth';
import { adminHeaders } from '../tenants';
import { runtimeConfig } from '../runtime-config';

export { ApiError, type RequestOptions } from '@dcms/core';

/**
 * The admin SPA's one API client: admin-api, with the ambient tenant header on every call.
 *
 * The wrapper itself (request-id origination, trace-id extraction, the single 401 replay,
 * the XHR upload path) lives in `@dcms/core`; what is app-specific is the base path
 * and the fact that these calls carry `X-Dcms-Tenant`.
 */
const client = createApiClient({
  base: runtimeConfig.adminApiBase,
  headers: adminHeaders,
  renew: renewSilently,
});

export const api = client;

/** API path (relative to base) for a media asset's bytes / a named variant. */
export function mediaContentPath(id: string, variant?: string): string {
  const q = variant ? `?variant=${encodeURIComponent(variant)}` : '';
  return `/admin/media/${id}/content${q}`;
}

/**
 * Fetch a protected resource with the bearer token and return an object URL.
 * Needed for media previews: <img src> can't carry the Authorization header, so
 * we fetch the bytes and hand back a blob: URL (caller revokes on unmount).
 */
export const fetchObjectUrl = client.fetchObjectUrl;
