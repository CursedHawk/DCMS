import { platformTest as test, expect, data } from '../fixtures/test';

/**
 * The operations console.
 *
 * <p>Its own origin, its own OIDC client and — since P3 — its own backend. What is worth
 * checking from outside is what the split changed: this console must not reach admin-api at
 * all, and an account with no platform permission has to be told plainly rather than shown an
 * empty shell it will read as broken.</p>
 */

test('shows the operator the platform, and talks only to platform-api and identity', async ({
  page,
  api,
}) => {
  await page.goto('/');

  await expect(page.getByRole('navigation', { name: 'Platform sections' })).toBeVisible();

  /*
   * The check that P3 actually landed. The console used to call three origins; a request to
   * `/api/admin/...` from here means the SPA switch was incomplete, and it would keep working
   * right up until the edge route is removed — at which point it would silently receive the
   * SPA's own index.html with a 200 and hand it to a query as a string.
   */
  const toAdminApi = api.requests.filter(
    (r) => r.path.startsWith('/api/admin') || r.path.startsWith('/api/') === false,
  );
  expect(toAdminApi.map((r) => r.path)).toEqual([]);
});

test.describe('an account with no platform permission', () => {
  test.use({ grants: [] });

  test('is told plainly where it should be instead', async ({ page }) => {
    await page.goto('/');

    await expect(page.getByRole('heading', { name: /for platform operators/i })).toBeVisible();
    await expect(page.getByText(/If you manage a workspace/)).toBeVisible();
  });
});

test('the tenant list says which workspaces are suspended', async ({ page }) => {
  await page.goto('/tenants');

  await expect(page.getByRole('cell', { name: data.TENANT.name })).toBeVisible();
  await expect(page.getByRole('cell', { name: data.OTHER_TENANT.name })).toBeVisible();
  // Scoped to the table: this page also renders a summary list beside it, and the status badge
  // appears in both.
  await expect(page.getByRole('table').getByText('Suspended')).toBeVisible();
});

test('the audit log fills the columns that used to be empty on every row', async ({ page }) => {
  await page.goto('/audit');

  /*
   * Scoped to the table throughout. `DataTable` renders both layouts — the table and the card
   * list it becomes below `md` — and hides one with CSS, so every one of these strings is in the
   * document twice and an unscoped locator is a strict-mode violation rather than a pass.
   */
  const table = page.getByRole('table');
  await expect(table.getByRole('cell', { name: 'tenant.suspend' })).toBeVisible();

  /*
   * "Who" and "Trace" were blank on every row for as long as this page had existed: it read
   * `actorDisplay` and `traceId` while admin-api projected `actor.display` and `correlationId`.
   * Reading from `obs.v_audit_recent` through platform-api is what fixed it, and nothing but a
   * rendered row says so.
   */
  await expect(table.getByRole('cell', { name: /Ada Lovelace/ })).toBeVisible();
  await expect(table.getByText('abcdef012345')).toBeVisible();

  // And it marks a record that came through a service hop rather than from a session, which is
  // the honest reading of a proxied write.
  await expect(table.getByText('via service')).toBeVisible();
});
