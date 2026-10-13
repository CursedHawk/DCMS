import { test, expect, data } from '../fixtures/test';

/**
 * The shell: what surrounds every page, and the three things it is responsible for getting
 * right — who you are, which workspace you are in, and what you may open.
 */

test('renders the workspace shell for a signed-in operator', async ({ page, api }) => {
  await page.goto('/');

  await expect(page.getByRole('heading', { name: /Welcome back/ })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Sections' })).toBeVisible();
  // The tenant header is not optional: without it admin-api resolves no tenant and every
  // request 400s. Assert it was actually sent rather than trusting that the page rendered.
  // (`/me/tenants` is deliberately the exception — it answers "which workspaces are yours",
  // which cannot be scoped to one of them.)
  const scoped = api.requestsTo('GET', '/api/admin/me/permissions').at(0);
  expect(scoped?.headers['x-dcms-tenant']).toBe(data.TENANT.slug);
});

test('draws the menu the server sent, including a plugin instance entry', async ({ page }) => {
  await page.goto('/');
  const nav = page.getByRole('navigation', { name: 'Sections' });

  // These come from the fixture's /admin/navigation, not from any list in the bundle.
  await expect(nav.getByRole('link', { name: 'Media' })).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Marketplace' })).toBeVisible();
  // Six admin destinations collapsed into one; the sections live behind it.
  await expect(nav.getByRole('link', { name: 'Settings' })).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Members' })).toHaveCount(0);

  // The point of a server-driven menu: an entry whose label is a name a tenant typed, which
  // has no translation key and could not have come from the hard-coded array this replaced.
  await expect(nav.getByRole('link', { name: 'Press room' })).toBeVisible();

  // And nothing it did not send.
  await expect(nav.getByRole('link', { name: 'Chat' })).toHaveCount(0);
});

test('groups the menu by kind of work, and an unknown section still shows its entries', async ({ page, api }) => {
  api.on('GET', '/api/admin/navigation', {
    items: [
      ...data.NAVIGATION.items,
      // A section this console has never heard of: drawn under Apps rather than dropped.
      { to: '/app/guestbook/overview', labelKey: null, icon: 'BookOpen', group: 'somewhere-new', label: 'Guestbooks' },
    ],
  });
  await page.goto('/');
  const nav = page.getByRole('navigation', { name: 'Sections' });

  await expect(nav.getByRole('heading')).toHaveText(['Create & manage', 'Apps', 'Extend', 'Workspace']);
  await expect(nav.getByRole('link', { name: 'Guestbooks' })).toBeVisible();
  // Installed plugins stay in the menu once the server has answered: it used to be drawn on
  // first paint from the bundle's list and then removed because the server's list lacked it.
  await expect(nav.getByRole('link', { name: 'Plugins' })).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Marketplace' })).toBeVisible();
});

test('navigates without a page reload and marks the current section', async ({ page }) => {
  await page.goto('/');
  const nav = page.getByRole('navigation', { name: 'Sections' });

  await nav.getByRole('link', { name: 'Media' }).click();

  await expect(page).toHaveURL(/\/media$/);
  await expect(nav.getByRole('link', { name: 'Media' })).toHaveAttribute('aria-current', 'page');
  await expect(nav.getByRole('link', { name: 'Dashboard' })).not.toHaveAttribute(
    'aria-current',
    'page',
  );
});

test('switching workspace changes the tenant header every later request carries', async ({
  page,
  api,
}) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: data.TENANT.name })).toBeVisible();

  await page.getByRole('button', { name: data.TENANT.name }).click();
  await page.getByRole('button', { name: data.OTHER_TENANT.name }).click();

  // The switcher reloads deliberately — permissions and every list are tenant-scoped — so the
  // assertion is about what the reloaded app sends, not about what is on screen.
  await expect(page.getByRole('button', { name: data.OTHER_TENANT.name })).toBeVisible();
  const after = api.requests
    .filter((r) => r.path === '/api/admin/me/permissions')
    .at(-1);
  expect(after?.headers['x-dcms-tenant']).toBe(data.OTHER_TENANT.slug);
});

test('switching workspace from a page of the old one lands on the dashboard', async ({ page }) => {
  // A route like /sites/<id> names something the next workspace does not have; reloading it
  // under the new tenant showed an error page.
  await page.goto('/media');
  await expect(page.getByText('logo.png')).toBeVisible();

  await page.getByRole('button', { name: data.TENANT.name }).click();
  await page.getByRole('button', { name: data.OTHER_TENANT.name }).click();

  await expect(page.getByRole('heading', { name: /Welcome back/ })).toBeVisible();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole('button', { name: data.OTHER_TENANT.name })).toBeVisible();
});

test.describe('a remembered workspace this account is not a member of', () => {
  // What localStorage still holds after switching to an account outside that workspace.
  test.use({ tenantSlug: 'initech' });

  test('falls back to one of the account\'s own workspaces, on the dashboard', async ({ page, api }) => {
    await page.goto('/media');

    await expect(page.getByRole('heading', { name: /Welcome back/ })).toBeVisible();
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole('button', { name: data.TENANT.name })).toBeVisible();
    const last = api.requestsTo('GET', '/api/admin/me/permissions').at(-1);
    expect(last?.headers['x-dcms-tenant']).toBe(data.TENANT.slug);
  });

  test.describe('as a SuperAdmin', () => {
    test.use({ superAdmin: true });

    test('keeps it: the platform console opens any workspace this way', async ({ page }) => {
      await page.goto('/media');
      await expect(page.getByText('logo.png')).toBeVisible();
      await page.waitForLoadState('networkidle');

      await expect(page).toHaveURL(/\/media$/);
      expect(await page.evaluate(() => localStorage.getItem('dcms.tenant'))).toBe('initech');
    });
  });
});

test.describe('storage notice', () => {
  // Every other spec starts with it dismissed: it is a fixed bar over the foot of every page.
  test.use({ dismissStorageNotice: false });

  test('appears once and stays dismissed', async ({ page }) => {
    await page.goto('/');

    const notice = page.getByRole('status').filter({ hasText: /stores only what it needs/i });
    await expect(notice).toBeVisible();
    await notice.getByRole('button', { name: 'Got it' }).click();
    await expect(notice).toHaveCount(0);

    // Dismissal is remembered per browser, not per page load.
    await page.reload();
    await expect(page.getByRole('status').filter({ hasText: /stores only what it needs/i })).toHaveCount(0);
  });
});

test.describe('the command palette', () => {
  test('finds a settings section by name, and remembers where you have been', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('navigation', { name: 'Sections' }).getByRole('link', { name: 'Media' }).click();
    await expect(page).toHaveURL(/\/media$/);
    await page.getByRole('navigation', { name: 'Sections' }).getByRole('link', { name: 'Marketplace' }).click();
    await expect(page).toHaveURL(/\/marketplace$/);

    await page.keyboard.press('ControlOrMeta+k');
    const palette = page.getByRole('dialog');
    // Grouped, with where you were before this page first.
    await expect(palette.getByRole('group', { name: 'Recent' }).getByRole('option', { name: 'Media' })).toBeVisible();
    await expect(palette.getByRole('group', { name: 'Apps' }).getByRole('option', { name: 'Forms' })).toBeVisible();

    // Members, Domains and the rest have no sidebar line; the palette is how you reach them.
    await palette.getByRole('combobox').fill('domains');
    await expect(palette.getByRole('group', { name: 'Recent' })).toHaveCount(0);
    await palette.getByRole('option', { name: 'Domains' }).click();
    await expect(page).toHaveURL(/\/settings\/domains$/);
  });
});
