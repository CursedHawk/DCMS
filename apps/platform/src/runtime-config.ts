import { createRuntimeConfig } from '@dcms/core';

/**
 * The platform console's runtime keys.
 *
 * Two API bases, because the console talks to two services. platform-api serves everything the
 * console does -- including the four areas admin-api actually performs, which it reaches on the
 * console's behalf -- and identity serves the user directory, which it owns and gates on the
 * global role rather than on this console's permission table. Both are same-origin behind the
 * edge, so they default to path prefixes and differ only in the prefix.
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
