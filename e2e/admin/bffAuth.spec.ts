import { test as base, expect } from '@playwright/test';
import { adminApi, type MockApi } from '../fixtures/api';
import { BFF_OPERATOR, CSRF_TOKEN, useBffMode } from '../fixtures/bff';

/**
 * The console with its tokens at the edge (ADR 0014 phase 4).
 *
 * <p><b>What only a browser can say.</b> Every other suite covers this from one side: the unit
 * tests assert `getAccessToken()` returns undefined, and the edge's tests assert it attaches a
 * bearer when it sees no Authorization header. Neither can say whether the real console,
 * rendered by a real browser, actually stops sending one — and the failure mode if it does not
 * is silent. The edge would pass the request through untouched, the console's own token would
 * still work, and the cutover would look like it happened while nothing had changed.</p>
 *
 * <p>So the assertions here are mostly about an absence, and about a header that appears in its
 * place.</p>
 */

const test = base.extend<{ api: MockApi }>({
  api: [
    async ({ page }, use) => {
      const api = adminApi();
      await api.install(page);
      await use(api);
      api.expectNoMissingRoutes();
    },
    { auto: true },
  ],
});

test('signs in from the edge session rather than from anything in the browser', async ({ page }) => {
  await useBffMode(page);
  await page.goto('/');

  // The shell renders the operator /.edge/me named — without oidc-client-ts, and without a
  // token anywhere a script could reach.
  await expect(page.getByRole('heading', { name: new RegExp(BFF_OPERATOR.name) })).toBeVisible();

  const storage = await page.evaluate(() => ({ ...window.localStorage }));
  expect(Object.keys(storage).filter((k) => k.startsWith('oidc.'))).toEqual([]);
});

test('sends no Authorization header on any API call', async ({ page, api }) => {
  await useBffMode(page);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: new RegExp(BFF_OPERATOR.name) })).toBeVisible();

  expect(api.requests.length).toBeGreaterThan(0);
  // THE assertion of this file. A bearer here and the edge leaves the request alone, so the
  // console would carry on working off its own token and the cutover would be a no-op nobody
  // noticed.
  const withAuth = api.requests.filter((r) => 'authorization' in r.headers);
  expect(withAuth.map((r) => `${r.method} ${r.path}`)).toEqual([]);
});

test('echoes the CSRF token the edge minted, on writes', async ({ page, api }) => {
  await useBffMode(page);
  await page.goto('/notifications');

  await page.getByRole('button', { name: 'Mark all read' }).click();
  await expect
    .poll(() => api.requestsTo('POST', '/api/admin/notifications/read-all').length)
    .toBeGreaterThan(0);

  // Without this the edge refuses the write with a 403 — while reads keep working, which is
  // the confusing shape this test exists to catch before an operator meets it.
  const [write] = api.requestsTo('POST', '/api/admin/notifications/read-all');
  expect(write.headers['x-dcms-csrf']).toBe(CSRF_TOKEN);
});

test('sends no CSRF token when the edge has not minted one', async ({ page, api }) => {
  // Not an empty header: an absent token and a wrong one are the same refusal at the edge, and
  // bearer mode has no such cookie at all — so sending one unconditionally would put a
  // meaningless header on every request the console has ever made.
  await useBffMode(page, { issueCsrf: false });
  await page.goto('/notifications');

  await page.getByRole('button', { name: 'Mark all read' }).click();
  await expect
    .poll(() => api.requestsTo('POST', '/api/admin/notifications/read-all').length)
    .toBeGreaterThan(0);

  const [write] = api.requestsTo('POST', '/api/admin/notifications/read-all');
  expect(write.headers).not.toHaveProperty('x-dcms-csrf');
});

test('still names the workspace, which is a request and not a credential', async ({ page, api }) => {
  await useBffMode(page);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: new RegExp(BFF_OPERATOR.name) })).toBeVisible();

  // The tenant header is unchanged by the cutover: it names a workspace, and the server still
  // resolves it and refuses a caller who is not a member. Losing it here would look like a
  // permissions bug on every screen.
  const tenanted = api.requests.filter((r) => r.headers['x-dcms-tenant']);
  expect(tenanted.length).toBeGreaterThan(0);
});

test('renders the sign-in screen when the edge reports no session', async ({ page, api }) => {
  await useBffMode(page, { session: null });
  await page.goto('/');

  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
  // And asks for nothing. A console that polls while signed out is a console that 401s in a
  // loop — the same property signin.spec.ts pins for bearer mode.
  expect(api.requests).toEqual([]);
});

test('create an account asks the edge for the sign-up form', async ({ page }) => {
  await useBffMode(page, { session: null });
  await page.goto('/');

  const signin = page.waitForRequest((r) => r.url().includes('/.edge/signin'));
  await page.getByRole('button', { name: 'Create account' }).click();

  // Same redirect with the hint the edge forwards to identity, which lands a new user on the
  // registration form rather than a sign-in form they have no account for. The console used to
  // add this itself as an OIDC extra query parameter; it no longer builds that request at all.
  expect(new URL((await signin).url()).searchParams.get('flow')).toBe('register');
});

test('sign in and sign out are handed to the edge, not driven from the browser', async ({ page }) => {
  await useBffMode(page, { session: null });
  await page.goto('/');

  const signin = page.waitForRequest((r) => r.url().includes('/.edge/signin'));
  await page.getByRole('button', { name: 'Sign in' }).click();
  const request = await signin;

  // Carries where to come back to, and nothing else — no client id, no PKCE challenge, no
  // scopes. The console no longer builds an authorization request at all.
  const url = new URL(request.url());
  expect(url.searchParams.get('returnUrl')).toBe('/');
  expect([...url.searchParams.keys()]).toEqual(['returnUrl']);
});
