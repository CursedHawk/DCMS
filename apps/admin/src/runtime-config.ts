/**
 * Runtime configuration for the admin SPA.
 *
 * Values are resolved in this order:
 *
 *   1. `window.__DCMS_CONFIG__`, injected into index.html by the container entrypoint.
 *   2. `import.meta.env.VITE_*`, for `vite dev` and for anyone who still builds with them.
 *   3. A development default.
 *
 * The point of (1) is that the built bundle is environment-agnostic, so the SAME image can be
 * promoted from dev to production. Baking the OIDC authority in at build time -- which is what
 * `import.meta.env` does -- meant an image built for `admin.dev.highgeek.eu` could never serve
 * `admin.highgeek.eu`, so "build once, deploy the tested digest" was not actually possible for
 * the one artifact users load in a browser.
 */

declare global {
  interface Window {
    __DCMS_CONFIG__?: Partial<Record<RuntimeConfigKey, string>>;
  }
}

type RuntimeConfigKey = 'oidcAuthority' | 'oidcClientId' | 'adminApiBase' | 'contentApiBase';

/**
 * An unsubstituted placeholder still looks like `__DCMS_FOO__`. That happens under `vite dev`
 * (nothing rewrites index.html) and would happen if the entrypoint were ever skipped -- in
 * which case falling through to the Vite env is far better than pointing the browser at a
 * literal `__DCMS_OIDC_AUTHORITY__` origin.
 */
function fromWindow(key: RuntimeConfigKey): string | undefined {
  const value = window.__DCMS_CONFIG__?.[key];
  if (!value || /^__DCMS_[A-Z0-9_]+__$/.test(value)) return undefined;
  return value;
}

export const runtimeConfig = {
  get oidcAuthority(): string {
    return fromWindow('oidcAuthority') ?? import.meta.env.VITE_OIDC_AUTHORITY ?? 'http://localhost:5001';
  },
  get oidcClientId(): string {
    return fromWindow('oidcClientId') ?? import.meta.env.VITE_OIDC_CLIENT_ID ?? 'dcms-admin-spa';
  },
  get adminApiBase(): string {
    return fromWindow('adminApiBase') ?? import.meta.env.VITE_ADMIN_API_BASE ?? '/api';
  },
  /** Empty string means "same origin", which is how the chat hub is reached behind Caddy. */
  get contentApiBase(): string {
    return fromWindow('contentApiBase') ?? import.meta.env.VITE_CONTENT_API_BASE ?? '';
  },
};
