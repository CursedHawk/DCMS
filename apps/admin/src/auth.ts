import { createAuth, createBffAuth } from '@dcms/core';
import { runtimeConfig } from './runtime-config';

/*
 * The admin SPA's authentication, in whichever of the two modes this deployment runs.
 *
 * BEARER is the original: authorization code + PKCE against identity, tokens in localStorage,
 * an Authorization header on every call. The flow itself — the StrictMode replay guard,
 * renew-on-read — lives in `@dcms/core` because the platform console needs exactly the same
 * behaviour.
 *
 * BFF is ADR 0014: the edge holds the tokens and attaches the bearer as it proxies, so nothing
 * here can read a credential and an XSS in this console cannot carry one away. Both modes are
 * built and tested; which one runs is a runtime value.
 *
 * The authority and client id below are read even in BFF mode, harmlessly — they are inert
 * there, and leaving them resolved means flipping the mode back needs no other change.
 */
/**
 * True when the edge holds this console's tokens (ADR 0014) instead of the browser.
 *
 * <p>Exported because two call sites legitimately need to know which world they are in: the
 * account API, whose base origin differs, and the headers helper, which sends a CSRF token
 * only in this mode. Everything else goes through the AuthClient and cannot tell.</p>
 */
export const isBffMode = runtimeConfig.authMode === 'bff';

const auth = isBffMode
  ? createBffAuth()
  : createAuth({
      authority: runtimeConfig.oidcAuthority,
      clientId: runtimeConfig.oidcClientId,
      scope: 'openid profile email roles dcms.admin offline_access',
    });

export const login = auth.login;
export const register = auth.register;
export const logout = auth.logout;
export const completeSignin = auth.completeSignin;
export const renewSilently = auth.renewSilently;
export const getUser = auth.getUser;
export const getAccessToken = auth.getAccessToken;
export const subscribeToAuth = auth.subscribe;
export const clearLocalSession = auth.clearLocalSession;
