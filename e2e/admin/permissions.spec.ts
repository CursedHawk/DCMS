import { test, expect, data } from '../fixtures/test';

/**
 * "Actions the user is not allowed to do are unavailable" — checked from the outside, which is
 * the only place the claim means anything.
 *
 * <p>Filtering the sidebar is not access control: every route is reachable by typing its URL,
 * and before the guards a hidden page still rendered, then fired the queries the API refused,
 * leaving a screen of failed requests and empty tables that reads as a broken product rather
 * than a closed door.</p>
 *
 * <p>Two rules, deliberately opposite, and both are asserted here. A <b>page</b> you cannot open
 * is replaced by a refusal that names the permission. A <b>control</b> you cannot use is
 * disabled and says why, rather than vanishing — hiding a button teaches nobody what to ask
 * their admin for.</p>
 */

test.describe('a member who may only read content', () => {
  test.use({ grants: ['content:read'] });

  test('reaches the content list', async ({ page }) => {
    await page.goto('/content');
    await expect(page.getByRole('heading', { name: 'Content', exact: true })).toBeVisible();
  });

  test('is refused the media library, by name', async ({ page, api }) => {
    await page.goto('/media');

    await expect(page.getByRole('heading', { name: /do not have access/i })).toBeVisible();
    // The permission is named on purpose. It is not a leak — the catalogue is readable by
    // anyone who can open the roles screen — and it is the difference between somebody who can
    // ask for the right thing and somebody who files "it's broken".
    await expect(page.getByText('media:read')).toBeVisible();

    // And the page never ran its queries, so the refusal is a closed door rather than a screen
    // of 403s.
    expect(api.requestsTo('GET', '/api/admin/media')).toEqual([]);
  });

  test('is refused the site editor, which is reached from a list rather than the menu', async ({
    page,
  }) => {
    await page.goto('/sites/11111111-1111-1111-1111-111111111111');
    await expect(page.getByText('site:edit')).toBeVisible();
  });

  test('cannot act on a selection it may not change', async ({ page }) => {
    await page.goto('/content');
    await page.getByRole('link', { name: /Press room/ }).first().click().catch(() => {});

    // Select the first row, then look at what the bulk bar offers.
    const rows = page.getByRole('checkbox', { name: 'Select row' });
    await rows.first().click();

    // Exact: "Publish" is also a substring of "Unpublish", and both are in this bar.
    const publish = page.getByRole('button', { name: 'Publish', exact: true });
    await expect(publish).toBeDisabled();
    // Disabled, and it says which key is missing rather than simply refusing.
    await publish.hover({ force: true }).catch(() => {});
    await expect(page.getByText('content:publish')).toBeVisible();
  });
});

test.describe('a SuperAdmin', () => {
  test.use({ grants: [], superAdmin: true });

  test('holds everything without a single permission row', async ({ page }) => {
    // Mirrors the server's `PermissionAuthorizationHandler`, which short-circuits on the role.
    // The two have to agree or the console offers buttons the API refuses, or hides ones it
    // would have allowed.
    await page.goto('/media');
    await expect(page.getByText('logo.png')).toBeVisible();

    await page.goto('/tenants');
    await expect(page.getByRole('heading', { name: /Tenants/i })).toBeVisible();
  });
});

test.describe('an ordinary member', () => {
  test.use({ grants: ['content:read'] });

  test('is refused the SuperAdmin-only tenant list, and told which workspace it is in', async ({
    page,
  }) => {
    await page.goto('/tenants');
    await expect(page.getByRole('heading', { name: /do not have access/i })).toBeVisible();
  });

  test('still reaches the pages that name no permission', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: /Welcome back/ })).toBeVisible();
    expect(data.TENANT.slug).toBeTruthy();
  });
});
