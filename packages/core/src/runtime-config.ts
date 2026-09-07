/**
 * Runtime configuration shared by the DCMS operator SPAs.
 *
 * Values are resolved in this order:
 *
 *   1. `window.__DCMS_CONFIG__`, injected into index.html by the container entrypoint.
 *   2. `import.meta.env.VITE_*`, for `vite dev` and for anyone who still builds with them.
 *   3. A development default supplied by the caller.
 *
 * The point of (1) is that the built bundle is environment-agnostic, so the SAME image can be
 * promoted from dev to production. Baking a value in at build time -- which is what
 * `import.meta.env` does -- meant an image built for `admin.dev.highgeek.eu` could never serve
 * `admin.highgeek.eu`, so "build once, deploy the tested digest" was not actually possible for
 * the one artifact users load in a browser.
 *
 * Each app declares its own key set (the admin SPA needs a content-API base; the platform SPA
 * needs a Grafana base) and calls `createRuntimeConfig` with it.
 */

declare global {
  interface Window {
    __DCMS_CONFIG__?: Record<string, string | undefined>;
  }
}

/**
 * An unsubstituted placeholder still looks like `__DCMS_FOO__`. That happens under `vite dev`
 * (nothing rewrites index.html) and would happen if the entrypoint were ever skipped -- in
 * which case falling through to the Vite env is far better than pointing the browser at a
 * literal `__DCMS_OIDC_AUTHORITY__` origin.
 */
const PLACEHOLDER = /^__DCMS_[A-Z0-9_]+__$/;

export function fromWindow(key: string): string | undefined {
  const value = window.__DCMS_CONFIG__?.[key];
  if (!value || PLACEHOLDER.test(value)) return undefined;
  return value;
}

/** One key's resolution: injected value, then the Vite env value, then the default. */
export interface RuntimeConfigEntry {
  /** The `window.__DCMS_CONFIG__` key. */
  key: string;
  /** `import.meta.env.VITE_*`, read by the caller (import.meta is per-module). */
  env?: string;
  /** Development fallback. An empty string means "same origin". */
  fallback: string;
}

/**
 * Builds a lazily-resolved config object. Getters rather than plain values, because
 * `window.__DCMS_CONFIG__` is set by an inline script in index.html and a module evaluated
 * before it would capture `undefined` permanently.
 */
export function createRuntimeConfig<K extends string>(
  entries: Record<K, RuntimeConfigEntry>,
): Record<K, string> {
  const config = {} as Record<K, string>;
  for (const name of Object.keys(entries) as K[]) {
    const entry = entries[name];
    Object.defineProperty(config, name, {
      enumerable: true,
      get: () => fromWindow(entry.key) ?? entry.env ?? entry.fallback,
    });
  }
  return config;
}
