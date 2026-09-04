import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts';

export interface AuthOptions {
  /** The identity service origin. Resolved at RUNTIME so one image serves every environment. */
  authority: string;
  /** The OpenIddict client id seeded for this SPA (`dcms-admin-spa`, `dcms-platform-spa`, …). */
  clientId: string;
  /**
   * Space-separated OIDC scopes. `offline_access` is load-bearing: OpenIddict only mints a
   * refresh token when it is among the GRANTED scopes, and without one `renewSilently` has
   * nothing to renew with — every session would end at the 10-minute access-token lifetime.
   */
  scope: string;
}

export interface AuthClient {
  userManager: UserManager;
  login(): Promise<void>;
  register(): Promise<void>;
  logout(): Promise<void>;
  completeSignin(): Promise<User>;
  renewSilently(): Promise<User | null>;
  getUser(): Promise<User | null>;
  getAccessToken(): Promise<string | undefined>;
}

/**
 * One OIDC client per SPA. A factory rather than a module-level singleton because the two
 * SPAs differ in exactly three values (client id, scopes, redirect origin) and in nothing
 * else — the memoisation subtleties below are identical for both and are the reason this is
 * shared code rather than a copy.
 */
export function createAuth(options: AuthOptions): AuthClient {
  const origin = window.location.origin;

  const userManager = new UserManager({
    authority: options.authority,
    client_id: options.clientId,
    redirect_uri: `${origin}/auth/callback`,
    post_logout_redirect_uri: `${origin}/`,
    response_type: 'code',
    scope: options.scope,
    userStore: new WebStorageStateStore({ store: window.localStorage }),
    automaticSilentRenew: true,
  });

  // signinRedirectCallback() exchanges the single-use authorization code for
  // tokens. Under React StrictMode the callback effect mounts twice, so a naive
  // call runs the exchange twice; the second attempt reuses the same code, which
  // OpenIddict rejects as a replay AND revokes the tokens just issued to the first
  // call — leaving the user signed out even though the identity cookie was set.
  // Memoise so the exchange runs exactly once and both mounts await one result.
  let signinCallback: Promise<User> | null = null;

  // automaticSilentRenew only schedules a renewal while the app is running: it
  // hangs off the "access token expiring" timer, which never fires for a token
  // that was already expired when the page loaded. So after the tab has been
  // closed longer than the access-token lifetime, getUser() hands back a stale
  // user — the shell renders "signed in" off the ID-token profile while every API
  // call 401s. Renew on read instead, using the refresh token (offline_access).
  let renewal: Promise<User | null> | null = null;

  function renewSilently(): Promise<User | null> {
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

  async function getUser(): Promise<User | null> {
    const user = await userManager.getUser();
    if (!user || !user.expired) return user;
    return renewSilently();
  }

  return {
    userManager,
    login: () => userManager.signinRedirect(),
    // Same authorization-code flow as login(), but carries a hint so the identity
    // server lands the user on the sign-up form instead of the sign-in form. The
    // flag rides along as returnUrl and survives the round-trip back to /connect/authorize.
    register: () => userManager.signinRedirect({ extraQueryParams: { dcms_flow: 'register' } }),
    logout: () => userManager.signoutRedirect(),
    completeSignin: () => (signinCallback ??= userManager.signinRedirectCallback()),
    renewSilently,
    getUser,
    async getAccessToken(): Promise<string | undefined> {
      const user = await getUser();
      return user?.access_token;
    },
  };
}
