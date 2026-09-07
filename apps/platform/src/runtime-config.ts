import { createRuntimeConfig } from '@dcms/core';

/**
 * The platform console's runtime keys.
 *
 * Three API bases rather than one because the console talks to three services, each the owner
 * of what it serves: platform-api for observability and ops, identity for the user directory,
 * admin-api for tenancy and audit. All three are same-origin behind the edge, so these default to
 * path prefixes and only differ from each other in the prefix.
 *
 * `environmentName` is not cosmetic. dev and production run the same image, and this console
 * can suspend a tenant — so which environment it points at has to be visible at all times.
 * See EnvironmentBand.
 */
export const runtimeConfig = createRuntimeConfig({
  oidcAuthority: {
    key: 'oidcAuthority',
    env: import.meta.env.VITE_OIDC_AUTHORITY,
    fallback: 'http://localhost:5001',
  },
  oidcClientId: {
    key: 'oidcClientId',
    env: import.meta.env.VITE_OIDC_CLIENT_ID,
    fallback: 'dcms-platform-spa',
  },
  platformApiBase: {
    key: 'platformApiBase',
    env: import.meta.env.VITE_PLATFORM_API_BASE,
    fallback: '/api/platform',
  },
  identityApiBase: {
    key: 'identityApiBase',
    env: import.meta.env.VITE_IDENTITY_API_BASE,
    fallback: '/api/identity',
  },
  adminApiBase: {
    key: 'adminApiBase',
    env: import.meta.env.VITE_ADMIN_API_BASE,
    fallback: '/api',
  },
  /** Origin of the tenant admin SPA, for deep links into one tenant's media and content. */
  adminBase: {
    key: 'adminBase',
    env: import.meta.env.VITE_ADMIN_BASE,
    fallback: 'http://localhost:5173',
  },
  /** Origin of Grafana. Empty disables the embedded dashboards rather than framing a 404. */
  grafanaBase: {
    key: 'grafanaBase',
    env: import.meta.env.VITE_GRAFANA_BASE,
    fallback: '',
  },
  environmentName: {
    key: 'environmentName',
    env: import.meta.env.VITE_ENVIRONMENT_NAME,
    fallback: 'development',
  },
});
