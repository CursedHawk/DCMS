import { anonymousTest as test, expect } from '../fixtures/test';

/**
 * What the app does before anybody has signed in — and the one part of the OIDC flow that
 * seeding a user cannot cover.
 *
 * <p>Every other spec puts a user straight into storage, because running OpenIddict, a database
 * and a login form in order to test what the console does *after* sign-in would make the whole
 * suite fail whenever the identity server was the thing that was broken. What that skips is
 * whether the redirect is built correctly, so this checks that directly: the real `UserManager`
 * builds the authorize URL and this asserts on it.</p>
 */

test('shows the sign-in screen and asks for nothing until it has a user', async ({ page, api }) => {
  await page.goto('/');

  await expect(page.getByRole('heading', { name: 'Welcome to DCMS' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();

  // Not one request. An unauthenticated console that polls is a console that 401s in a loop.
  expect(api.requests).toEqual([]);
});

test('sign in leaves for the identity server with a code + PKCE request', async ({ page }) => {
  await page.goto('/');

  // The authorize endpoint does not exist in this run, so the navigation fails — after the URL
  // has been built, which is the part under test.
  const request = page.waitForRequest((r) => r.url().includes('/connect/authorize'));
  await page.getByRole('button', { name: 'Sign in' }).click();

  const url = new URL((await request).url());
  expect(url.origin).toBe('http://localhost:5001');
  expect(url.searchParams.get('client_id')).toBe('dcms-admin-spa');
  expect(url.searchParams.get('response_type')).toBe('code');
  expect(url.searchParams.get('code_challenge_method')).toBe('S256');
  expect(url.searchParams.get('code_challenge')).toBeTruthy();
  expect(url.searchParams.get('redirect_uri')).toBe('http://127.0.0.1:5173/auth/callback');

  /*
   * `offline_access` is load-bearing and easy to lose. OpenIddict only mints a refresh token
   * when it is among the GRANTED scopes, and without one the silent renew has nothing to renew
   * with — every session would end at the ten-minute access-token lifetime, which reads to a
   * user as "it logs me out constantly" and to a developer as nothing at all.
   */
  expect(url.searchParams.get('scope')?.split(' ')).toEqual(
    expect.arrayContaining(['openid', 'dcms.admin', 'offline_access']),
  );
});

test('create an account asks the identity server for the sign-up form', async ({ page }) => {
  await page.goto('/');

  const request = page.waitForRequest((r) => r.url().includes('/connect/authorize'));
  await page.getByRole('button', { name: 'Create account' }).click();

  // Same authorization-code flow, with the hint that lands the visitor on registration rather
  // than on a sign-in form they have no account for.
  expect(new URL((await request).url()).searchParams.get('dcms_flow')).toBe('register');
});
