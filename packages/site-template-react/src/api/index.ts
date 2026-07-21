/**
 * Tenant API client.
 *
 * In this template this re-exports the generic `@dcms/api-client` runtime so the
 * project builds standalone. When you download the client/starter for a specific
 * tenant, this folder is REPLACED by a fully-typed, self-contained client
 * (`api.<instanceSlug>.<contentType>.list()`), exposing the same
 * `createTenantClient` entry point — so the rest of the app is unaffected.
 */
export { createTenantClient } from '@dcms/api-client';
export type * from '@dcms/api-client';
