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

/**
 * The bits of a signed-in operator every console reads: `sub` identifies them to the
 * notification and site hubs, `name`/`email` render the avatar menu.
 *
 * <p>Declared here rather than reusing oidc-client-ts's `User` because a BFF session
 * (ADR 0014) has no tokens and no ID token to take a profile from — it has whatever the edge
 * says at `/.edge/me`. `User` is structurally assignable to this, so bearer mode needs no
 * mapping and no component had to change.</p>
 */
export interface AuthSession {
  profile: { sub: string; name?: string; email?: string };
}

/**
 * What a console needs of its authentication, in the two shapes DCMS has: tokens held by the
 * browser (bearer) and tokens held by the edge (BFF). See ADR 0014.
 */
export interface AuthClient {
  login(): Promise<void>;
  register(): Promise<void>;
  logout(): Promise<void>;
  completeSignin(): Promise<AuthSession | null>;
  renewSilently(): Promise<AuthSession | null>;
  getUser(): Promise<AuthSession | null>;
  /** The bearer to send, or undefined in BFF mode — where the edge attaches it instead. */
  getAccessToken(): Promise<string | undefined>;
  /** Called when the session appears or disappears. Returns an unsubscribe. */
  subscribe(onChange: (user: AuthSession | null) => void): () => void;
  /**
   * Drops whatever this client holds locally, without asking the server to end anything. The
   * last resort when an account no longer exists and a proper sign-out cannot complete.
   */
  clearLocalSession(): Promise<void>;
}

/**
 * Bearer mode additionally exposes the raw `UserManager`. The platform console still drives it
 * directly, and this is what lets it keep doing so while the admin console moves off it.
 */
export interface BearerAuthClient extends AuthClient {
  userManager: UserManager;
}

/**
 * One OIDC client per SPA. A factory rather than a module-level singleton because the two
 * SPAs differ in exactly three values (client id, scopes, redirect origin) and in nothing
 * else — the memoisation subtleties below are identical for both and are the reason this is
 * shared code rather than a copy.
 */
export function createAuth(options: AuthOptions): BearerAuthClient {
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
    subscribe(onChange) {
      const onLoaded = (user: User) => onChange(user);
      const onUnloaded = () => onChange(null);
      // A background renewal that failed leaves an unusable token in the store. Dropping it is
      // what makes the shell show the sign-in screen instead of 401-ing forever.
      const onRenewError = (err: unknown) => {
        console.warn('OIDC silent renew failed; signing out locally.', err);
        void userManager.removeUser();
      };
      userManager.events.addUserLoaded(onLoaded);
      userManager.events.addUserUnloaded(onUnloaded);
      userManager.events.addSilentRenewError(onRenewError);
      return () => {
        userManager.events.removeUserLoaded(onLoaded);
        userManager.events.removeUserUnloaded(onUnloaded);
        userManager.events.removeSilentRenewError(onRenewError);
      };
    },
    clearLocalSession: () => userManager.removeUser(),
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

/**
 * The name of the readable half of the edge's double-submit CSRF pair, and the header the
 * console echoes it in. Mirrors `BffGuard` on the edge — the two have to agree, and there is
 * no build that checks them against each other, so both spell it out and say so.
 */
export const CSRF_COOKIE = 'dcms.csrf';
export const CSRF_HEADER = 'X-Dcms-Csrf';

/**
 * The CSRF header to send with a state-changing request, or nothing when there is no token.
 *
 * <p>Nothing, rather than an empty header: the edge refuses a write whose token does not
 * verify, and an absent header and a wrong one are the same refusal. Sending nothing keeps
 * bearer mode — where there is no such cookie — from putting a meaningless header on every
 * request.</p>
 */
export function csrfHeader(): Record<string, string> {
  // The name contains a dot, which is a regex metacharacter — unescaped, `dcmsXcsrf` would
  // match too. Escaped here rather than spelled as a literal so the constant stays the one
  // place the name is written.
  const name = CSRF_COOKIE.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`));
  return match ? { [CSRF_HEADER]: decodeURIComponent(match[1]) } : {};
}

export interface BffAuthOptions {
  /**
   * Where the edge serves its own endpoints. `/.edge` everywhere; a parameter because a test
   * has to be able to point it somewhere else, not because a deployment does.
   */
  basePath?: string;
}

/**
 * A console whose tokens live at the edge (ADR 0014).
 *
 * <p>There is no credential here to hold, renew or accidentally read: the session is an
 * HttpOnly cookie the browser attaches and this code cannot see. What is left is navigation —
 * sign-in and sign-out are redirects the edge handles — and one endpoint that says who the
 * cookie belongs to.</p>
 */
export function createBffAuth(options: BffAuthOptions = {}): AuthClient {
  const base = options.basePath ?? '/.edge';
  const listeners = new Set<(user: AuthSession | null) => void>();
  let current: AuthSession | null = null;

  /** Notifies only on a real change, so a poll or a retry does not churn the shell. */
  function publish(user: AuthSession | null): AuthSession | null {
    const changed = (current?.profile.sub ?? null) !== (user?.profile.sub ?? null);
    current = user;
    if (changed) {
      for (const listener of listeners) listener(user);
    }
    return user;
  }

  /**
   * Who the session belongs to, or null when there is not one.
   *
   * <p>A 401 here is the normal signed-out answer rather than an error — the edge deliberately
   * answers it instead of redirecting, because a redirect to identity's login page comes back
   * as HTML that a `fetch` caller cannot use.</p>
   */
  async function me(): Promise<AuthSession | null> {
    try {
      const response = await fetch(`${base}/me`, { headers: { Accept: 'application/json' } });
      if (!response.ok) return publish(null);
      const body = (await response.json()) as { sub?: string; name?: string; email?: string };
      if (!body.sub) return publish(null);
      return publish({ profile: { sub: body.sub, name: body.name, email: body.email } });
    } catch {
      // The network, not the session. Leaving the shell signed in is the kinder wrong answer:
      // every request will fail visibly anyway, and signing the operator out of a page they
      // are working in because one request did not leave the machine is worse.
      return current;
    }
  }

  /** Only ever a path on this origin, so a crafted returnUrl cannot become an open redirect. */
  function returnHere(): string {
    return encodeURIComponent(window.location.pathname + window.location.search);
  }

  return {
    login: async () => {
      window.location.assign(`${base}/signin?returnUrl=${returnHere()}`);
    },
    register: async () => {
      window.location.assign(`${base}/signin?flow=register&returnUrl=${returnHere()}`);
    },
    logout: async () => {
      window.location.assign(`${base}/signout`);
    },
    // The edge owns the OIDC callback, at /.edge/signin-oidc, so the SPA never sees a code to
    // exchange and its /auth/callback route is dead in this mode. Re-reading the session is
    // the honest answer to "did a sign-in just complete".
    completeSignin: me,
    renewSilently: me,
    getUser: me,
    // The whole point: there is no token here to hand out. Callers that ask get undefined and
    // send no Authorization header, which is exactly what tells the edge to attach its own.
    getAccessToken: async () => undefined,
    subscribe(onChange) {
      listeners.add(onChange);
      return () => listeners.delete(onChange);
    },
    // Nothing is held locally to clear. The cookie is HttpOnly and only the edge can end it.
    clearLocalSession: async () => {},
  };
}
