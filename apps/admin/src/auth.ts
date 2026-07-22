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

// automaticSilentRenew only schedules a renewal while the app is running: it
// hangs off the "access token expiring" timer, which never fires for a token
// that was already expired when the page loaded. So after the tab has been
// closed longer than the access-token lifetime, getUser() hands back a stale
// user — the shell renders "signed in" off the ID-token profile while every API
// call 401s. Renew on read instead, using the refresh token (offline_access).
let renewal: Promise<User | null> | null = null;

export function renewSilently(): Promise<User | null> {
  renewal ??= userManager
    .signinSilent()
    .catch(async (err: unknown) => {
      // Refresh token expired/revoked, or no session at the identity server:
      // drop the stale user so the UI falls back to the sign-in screen.
      console.warn('OIDC silent renew failed; signing out locally.', err);
      await userManager.removeUser();
      return null;
    })
    .finally(() => {
      renewal = null;
    });
  return renewal;
}

/** The current user, silently renewed first if its access token has expired. */
export async function getUser(): Promise<User | null> {
  const user = await userManager.getUser();
  if (!user || !user.expired) return user;
  return renewSilently();
}

export async function getAccessToken(): Promise<string | undefined> {
  const user = await getUser();
  return user?.access_token;
}
