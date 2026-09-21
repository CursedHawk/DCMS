import { test as base, expect } from '@playwright/test';
import { adminApi, platformApi, type MockApi } from './api';
import { useBffMode } from './bff';
import { adminHub, platformHub, type HubMock } from './hub';
import { PLATFORM_CLIENT_ID, signIn } from './oidc';
import * as data from './data';

export { expect, data };

interface ConsoleFixtures {
  /**
   * The permission keys the signed-in operator holds. `test.use({ grants: [...] })` in a
   * describe block is how the permission specs sign in as somebody narrower.
   *
   * Named `grants` rather than `permissions` because Playwright already owns a context option
   * by that name — the browser capabilities like geolocation — and overriding it makes every
   * test fail in `browser.newContext` before a line of the spec runs.
   */
  grants: string[] | null;
  superAdmin: boolean;
  /** The workspace slug the admin SPA sends as `X-Dcms-Tenant`. */
  tenantSlug: string;
  /** Off for the one spec that is about the notice itself. */
  dismissStorageNotice: boolean;
  api: MockApi;
  hub: HubMock;
}

/**
 * The admin console, signed in, with a faked server and a faked hub.
 *
 * <p>Signed in by the EDGE since ADR 0014 phase 5: `useBffMode` serves the console the way its
 * container entrypoint does and stands in for `/.edge/me`. There is no seeded OIDC user any
 * more, because there is no bearer mode left to seed one for — the admin console holds no
 * tokens. `signIn` and the OIDC constants are still used by the platform console below, which
 * has not moved.</p>
 *
 * <p>`api` is an <b>auto</b> fixture. Both the route handlers and the session have to be in
 * place before the page navigates, and Playwright only runs a fixture a test actually depends
 * on — so a spec that destructured just `{ page }` would render the sign-in screen and fail on
 * a locator, which reads as a bug in the app rather than a missing dependency. Auto removes the
 * trap; specs still name `api` when they want to assert on what was sent.</p>
 *
 * <p>Teardown asserts the console called nothing the fixtures do not answer. That is the check
 * that keeps a mocked suite honest: without it, a screen whose query silently 501s renders its
 * empty state and every assertion about "no rows" passes for the wrong reason.</p>
 */
export const test = base.extend<ConsoleFixtures>({
  grants: [null, { option: true }],
  superAdmin: [false, { option: true }],
  tenantSlug: [data.TENANT.slug, { option: true }],
  dismissStorageNotice: [true, { option: true }],

  api: [
    async ({ page, grants, superAdmin, tenantSlug, dismissStorageNotice }, use) => {
      const api = adminApi({ permissions: grants ?? undefined, isSuperAdmin: superAdmin });
      await api.install(page);
      await useBffMode(page, { tenantSlug, dismissStorageNotice });
      await use(api);
      api.expectNoMissingRoutes();
    },
    { auto: true },
  ],

  hub: async ({ page, api }, use) => {
    // Depends on `api` only for ordering: the socket must be routed before the shell mounts,
    // and the shell does not mount until the seeded user is in place.
    void api;
    await use(await adminHub(page));
  },
});

/** The platform console, on its own origin, with its own client id and hub. */
export const platformTest = base.extend<ConsoleFixtures>({
  grants: [null, { option: true }],
  superAdmin: [false, { option: true }],
  tenantSlug: ['', { option: true }],
  dismissStorageNotice: [true, { option: true }],

  api: [
    async ({ page, grants, superAdmin }, use) => {
      const api = platformApi({ permissions: grants ?? undefined, isSuperAdmin: superAdmin });
      await api.install(page);
      await signIn(page, { clientId: PLATFORM_CLIENT_ID });
      await use(api);
      api.expectNoMissingRoutes();
    },
    { auto: true },
  ],

  hub: async ({ page, api }, use) => {
    void api;
    await use(await platformHub(page));
  },
});

/**
 * No user seeded — for the specs that are about what an app does before anyone has signed in.
 * The mock server is still installed, so a stray request is still recorded rather than escaping
 * to a proxy with nothing behind it.
 */
/** The admin console with no session: the edge answers /.edge/me with a 401. */
export const anonymousTest = base.extend<{ api: MockApi }>({
  api: [
    async ({ page }, use) => {
      const api = adminApi();
      await api.install(page);
      await useBffMode(page, { session: null });
      await use(api);
    },
    { auto: true },
  ],
});
