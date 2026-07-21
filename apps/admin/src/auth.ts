import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts';

// OIDC config. authority points at the identity service; in dev that is the
// compose-mapped port 5001. Override via Vite env (VITE_OIDC_*) for other
// environments. Tokens are obtained with authorization code + PKCE.
const authority = import.meta.env.VITE_OIDC_AUTHORITY ?? 'http://localhost:5001';
const origin = window.location.origin;

export const userManager = new UserManager({
  authority,
  client_id: import.meta.env.VITE_OIDC_CLIENT_ID ?? 'dcms-admin-spa',
  redirect_uri: `${origin}/auth/callback`,
  post_logout_redirect_uri: `${origin}/`,
  response_type: 'code',
  scope: 'openid profile email roles dcms.admin offline_access',
  userStore: new WebStorageStateStore({ store: window.localStorage }),
  automaticSilentRenew: true,
});

export function login(): Promise<void> {
  return userManager.signinRedirect();
}

export function logout(): Promise<void> {
  return userManager.signoutRedirect();
}

// signinRedirectCallback() exchanges the single-use authorization code for
// tokens. Under React StrictMode the callback effect mounts twice, so a naive
// call runs the exchange twice; the second attempt reuses the same code, which
// OpenIddict rejects as a replay AND revokes the tokens just issued to the first
// call — leaving the user signed out even though the identity cookie was set.
// Memoise so the exchange runs exactly once and both mounts await one result.
let signinCallback: Promise<User> | null = null;

export function completeSignin(): Promise<User> {
  signinCallback ??= userManager.signinRedirectCallback();
  return signinCallback;
}

export function getUser(): Promise<User | null> {
  return userManager.getUser();
}

export async function getAccessToken(): Promise<string | undefined> {
  const user = await userManager.getUser();
  return user?.access_token;
}
