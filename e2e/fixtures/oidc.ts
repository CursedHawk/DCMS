import type { Page } from '@playwright/test';

/**
 * Puts a signed-in user in place before the app's first line of JavaScript runs.
 *
 * <p>Both SPAs read their user from `oidc-client-ts`, which keeps it in localStorage under
 * `oidc.user:{authority}:{client_id}` as the JSON that `User.toStorageString()` writes. Seeding
 * that record is the whole of "signing in" from the app's point of view: `getUser()` finds a
 * non-expired user and never goes near the identity server.</p>
 *
 * <p>Driving the real authorization-code flow instead would mean running OpenIddict, a database
 * and a login form in order to test what the console does <em>after</em> sign-in — and would
 * make every spec here fail whenever the identity server was the thing that was broken. The one
 * thing that seeding cannot cover, whether the redirect to `/connect/authorize` is built
 * correctly, is asserted directly in `admin/signin.spec.ts` against the real UserManager.</p>
 *
 * <p>`expires_at` is a day out. It has to be comfortably beyond the 60-second "expiring" mark,
 * or `automaticSilentRenew` schedules a renewal that fires mid-test against a server that is
 * not there.</p>
 */
export interface SeedUserOptions {
  authority?: string;
  clientId: string;
  sub?: string;
  name?: string;
  email?: string;
  /** Extra profile claims — `role`, for the SuperAdmin cases. */
  profile?: Record<string, unknown>;
}

export const ADMIN_CLIENT_ID = 'dcms-admin-spa';
export const PLATFORM_CLIENT_ID = 'dcms-platform-spa';
export const AUTHORITY = 'http://localhost:5001';

/** The signed-in operator every spec uses unless it says otherwise. */
export const OPERATOR_SUB = '11111111-1111-1111-1111-111111111111';

export function userStorage(options: SeedUserOptions): { key: string; value: string } {
  const authority = options.authority ?? AUTHORITY;
  const expiresAt = Math.floor(Date.now() / 1000) + 24 * 60 * 60;

  return {
    key: `oidc.user:${authority}:${options.clientId}`,
    value: JSON.stringify({
      id_token: 'e2e.id-token',
      session_state: null,
      // Not a real JWT. Nothing in the browser parses it — the app forwards it as a bearer
      // header, and the fixtures answer without looking. Identity claims come from `profile`,
      // which is what oidc-client-ts exposes as `user.profile`.
      access_token: 'e2e.access-token',
      refresh_token: 'e2e.refresh-token',
      token_type: 'Bearer',
      scope: 'openid profile email roles offline_access',
      profile: {
        sub: options.sub ?? OPERATOR_SUB,
        name: options.name ?? 'Ada Lovelace',
        email: options.email ?? 'ada@example.test',
        ...options.profile,
      },
      expires_at: expiresAt,
    }),
  };
}

/** Seeds the user, and the tenant selection the admin SPA sends as `X-Dcms-Tenant`. */
export async function signIn(
  page: Page,
  options: SeedUserOptions & { tenantSlug?: string; dismissStorageNotice?: boolean },
): Promise<void> {
  const { key, value } = userStorage(options);
  const tenant = options.tenantSlug;
  // The storage notice is a fixed bar across the foot of every page until it is dismissed, and
  // it sits over the controls at the bottom of a list. Pre-dismissed so no spec has to close it
  // first — `shell.spec.ts` turns this off to test the notice itself. It is an option here
  // rather than something a spec undoes later because a page-level init script re-runs on every
  // navigation, so a spec that cleared the key would find it set again after one reload.
  const dismissNotice = options.dismissStorageNotice ?? true;

  await page.addInitScript(
    ([storageKey, storageValue, tenantSlug, dismiss]) => {
      window.localStorage.setItem(storageKey as string, storageValue as string);
      // Only when nothing has chosen one. An init script re-runs on every navigation, and the
      // workspace switcher reloads the page deliberately — so writing this unconditionally
      // would put the original workspace back on the way in and silently undo the switch.
      if (tenantSlug && !window.localStorage.getItem('dcms.tenant')) {
        window.localStorage.setItem('dcms.tenant', tenantSlug as string);
      }
      if (dismiss) window.localStorage.setItem('dcms.storage-notice', '1');
    },
    [key, value, tenant ?? '', dismissNotice ? '1' : ''] as const,
  );
}

/** Leaves storage empty, so the app renders its sign-in screen. */
export async function signedOut(page: Page): Promise<void> {
  await page.addInitScript(() => window.localStorage.clear());
}

/**
 * The identity server's discovery document, and a stand-in for its authorize page.
 *
 * <p>`signinRedirect()` does not build a URL from configuration — it fetches
 * `/.well-known/openid-configuration` first and reads `authorization_endpoint` out of it. With
 * nothing answering, the redirect never happens and a test asserting on the authorize URL waits
 * for a request that is never made. That failure looks exactly like "the sign-in button is
 * broken", which is the wrong thing for a test to say when the truth is "no identity server is
 * running", so the document is stubbed rather than the button being trusted.</p>
 *
 * <p>The authorize endpoint itself answers with a page, so the browser lands somewhere instead
 * of on a connection error. What it returns does not matter; the assertion is on the request.</p>
 */
export async function stubOidcDiscovery(page: Page, authority = AUTHORITY): Promise<void> {
  await page.route('**/.well-known/openid-configuration', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        issuer: authority,
        authorization_endpoint: `${authority}/connect/authorize`,
        token_endpoint: `${authority}/connect/token`,
        userinfo_endpoint: `${authority}/connect/userinfo`,
        end_session_endpoint: `${authority}/connect/logout`,
        jwks_uri: `${authority}/.well-known/jwks`,
        response_types_supported: ['code'],
        code_challenge_methods_supported: ['S256'],
        grant_types_supported: ['authorization_code', 'refresh_token'],
      }),
    }),
  );

  await page.route('**/connect/authorize*', (route) =>
    route.fulfill({ status: 200, contentType: 'text/html', body: '<title>identity</title>' }),
  );
}
