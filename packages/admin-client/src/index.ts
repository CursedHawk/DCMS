/**
 * `@dcms/admin-client` — the runtime every DCMS operator SPA talks to its APIs with.
 *
 * Nothing here is app-specific: each SPA supplies its own base paths, OIDC client id and
 * scopes, and builds its own `api` objects from `createApiClient`. That is what lets the
 * platform SPA speak to three origins (platform-api, identity, admin-api) using one wrapper.
 */
export * from './runtime-config';
export * from './auth';
export * from './http';
export * from './permissions';
