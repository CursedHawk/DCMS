import { createBffAuth } from '@dcms/core';

/*
 * The admin console's authentication: a session the edge holds, and nothing in this browser.
 *
 * <p>There is no token here to read, renew or leak — sign-in and sign-out are redirects the
 * edge handles, and it attaches the bearer as it proxies. That is the whole of SEC-10's
 * residual, and ADR 0014 is the design.</p>
 *
 * <p>Until phase 5 this file chose between two modes at runtime. The bearer one is gone: the
 * `dcms-admin-spa` client no longer holds the `dcms.admin` scope, so a browser cannot obtain a
 * token for admin-api even if something here asked for one. `@dcms/core` still exports
 * `createAuth` for the platform console, which has not moved yet.</p>
 */
const auth = createBffAuth();

export const login = auth.login;
export const register = auth.register;
export const logout = auth.logout;
export const renewSilently = auth.renewSilently;
export const getUser = auth.getUser;
export const subscribeToAuth = auth.subscribe;
export const clearLocalSession = auth.clearLocalSession;
