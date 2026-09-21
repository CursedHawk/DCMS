import { createRuntimeConfig } from '@dcms/core';

/**
 * The admin SPA's runtime keys. Resolution order and the reason for it live in
 * `@dcms/core`'s runtime-config module; only the key set is app-specific.
 *
 * `import.meta.env` is read here rather than in the package because Vite substitutes it
 * per-module at build time — a package reading it would capture the package's own build.
 */
export const runtimeConfig = createRuntimeConfig({
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
