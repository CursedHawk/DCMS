import { createAuth } from '@dcms/core';
import { runtimeConfig } from './runtime-config';

/*
 * The console's OIDC client.
 *
 * Its own client id, not the admin SPA's: the two are served from different hosts, and sharing
 * one would let a token minted for the admin host be replayed into this one's callback.
 *
 * It asks for dcms.admin alongside dcms.platform because the console calls three APIs directly
 * — there is no service-to-service hop in this design, so every action is attributable to the
 * person who took it rather than to a service principal. The scopes grant nothing on their own;
 * every endpoint behind them is gated on SuperAdmin or a platform permission server-side.
 */
const auth = createAuth({
  authority: runtimeConfig.oidcAuthority,
  clientId: runtimeConfig.oidcClientId,
  scope: 'openid profile email roles dcms.platform dcms.admin offline_access',
});

export const login = auth.login;
export const logout = auth.logout;
export const completeSignin = auth.completeSignin;
export const renewSilently = auth.renewSilently;
export const getAccessToken = auth.getAccessToken;
export const userManager = auth.userManager;
