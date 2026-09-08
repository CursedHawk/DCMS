import { createAuth } from '@dcms/core';
import { runtimeConfig } from './runtime-config';

/*
 * The console's OIDC client.
 *
 * Its own client id, not the admin SPA's: the two are served from different hosts, and sharing
 * one would let a token minted for the admin host be replayed into this one's callback.
 *
 * dcms.platform and nothing else. This console holds no admin-api scope: certificates, the
 * platform bell, tenant lifecycle and the analytics prune are asked of platform-api, which
 * checks the operator's platform permission and forwards on a service token, propagating the
 * operator so the audit record still names the person rather than the service.
 *
 * The scope grants nothing on its own; every endpoint behind it is gated on SuperAdmin or a
 * platform permission server-side.
 */
const auth = createAuth({
  authority: runtimeConfig.oidcAuthority,
  clientId: runtimeConfig.oidcClientId,
  scope: 'openid profile email roles dcms.platform offline_access',
});

export const login = auth.login;
export const logout = auth.logout;
export const completeSignin = auth.completeSignin;
export const renewSilently = auth.renewSilently;
export const getAccessToken = auth.getAccessToken;
export const userManager = auth.userManager;
