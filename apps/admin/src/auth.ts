import { createAuth } from '@dcms/admin-client';
import { runtimeConfig } from './runtime-config';

/*
 * The admin SPA's OIDC client. The authority points at the identity service; in dev that is
 * the compose-mapped port 5001. Resolved at RUNTIME (see runtime-config.ts) rather than baked
 * in at build time, so one image serves every environment. Tokens are obtained with
 * authorization code + PKCE.
 *
 * The flow itself — the StrictMode replay guard, renew-on-read — lives in
 * `@dcms/admin-client` because the platform SPA needs exactly the same behaviour.
 */
const auth = createAuth({
  authority: runtimeConfig.oidcAuthority,
  clientId: runtimeConfig.oidcClientId,
  scope: 'openid profile email roles dcms.admin offline_access',
});

export const userManager = auth.userManager;
export const login = auth.login;
export const register = auth.register;
export const logout = auth.logout;
export const completeSignin = auth.completeSignin;
export const renewSilently = auth.renewSilently;
export const getUser = auth.getUser;
export const getAccessToken = auth.getAccessToken;
