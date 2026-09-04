import { createRuntimeConfig } from '@dcms/admin-client';

/**
 * The admin SPA's runtime keys. Resolution order and the reason for it live in
 * `@dcms/admin-client`'s runtime-config module; only the key set is app-specific.
 *
 * `import.meta.env` is read here rather than in the package because Vite substitutes it
 * per-module at build time — a package reading it would capture the package's own build.
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
    fallback: 'dcms-admin-spa',
  },
  adminApiBase: {
    key: 'adminApiBase',
    env: import.meta.env.VITE_ADMIN_API_BASE,
    fallback: '/api',
  },
  /** Empty string means "same origin", which is how the chat hub is reached behind the edge. */
  contentApiBase: {
    key: 'contentApiBase',
    env: import.meta.env.VITE_CONTENT_API_BASE,
    fallback: '',
  },
});
