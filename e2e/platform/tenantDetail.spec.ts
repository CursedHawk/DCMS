import { platformTest as test, expect, data } from '../fixtures/test';

/**
 * One tenant's page: reached from the list, and holding what the tenant has, what it stores and
 * who is in it, beside what can be done to it.
 */
test('the list opens a tenant, and the list shows what each one stores', async ({ page }) => {
  await page.goto('/tenants');
  const row = page.getByRole('row').filter({ hasText: data.TENANT.slug });
  await expect(row).toContainText('6.0 MB');
  // The actions moved to the tenant's page; the list is for finding one.
  await expect(row.getByRole('button', { name: 'Suspend' })).toHaveCount(0);

  await row.getByRole('link', { name: new RegExp(data.TENANT.slug) }).click();
  await expect(page).toHaveURL(new RegExp(`/tenants/${data.TENANT.tenantId}$`));
  await expect(page.getByRole('heading', { name: data.TENANT.name, level: 1 })).toBeVisible();
});

test('shows what the tenant stores and who and what it has', async ({ page }) => {
  await page.goto(`/tenants/${data.TENANT.tenantId}`);

  await expect(page.getByText('9 / 12')).toBeVisible();
  await expect(page.getByRole('meter', { name: 'Media files' })).toBeVisible();

  const usage = page.locator('section').filter({ has: page.getByRole('heading', { name: 'Data usage' }) });
  await expect(usage).toContainText('6.0 MB');
  await usage.getByRole('button', { name: /Dynamic Apps/ }).click();
  await expect(usage.getByText('12,400 rows')).toBeVisible();

  await expect(page.getByText('www.acme.example')).toBeVisible();
  await expect(page.getByText('Not verified')).toBeVisible();
  await expect(page.getByText('owner@acme.example')).toBeVisible();
  await expect(page.getByText('Press room')).toBeVisible();
});

test('Measure now asks admin-api for this tenant', async ({ page, api }) => {
  await page.goto(`/tenants/${data.TENANT.tenantId}`);
  await page.getByRole('button', { name: 'Measure now' }).click();

  await expect(page.getByText('Usage measured')).toBeVisible();
  expect(api.requestsTo('POST', `/api/platform/tenants/${data.TENANT.tenantId}/usage/recalculate`)).toHaveLength(1);
});

test('a tenant never measured says so', async ({ page, api }) => {
  api.on('GET', '/api/platform/tenants/:id', { ...data.platformTenantDetail(data.PLATFORM_TENANTS[0]), usage: null });
  await page.goto(`/tenants/${data.TENANT.tenantId}`);
  await expect(page.getByText('Not measured yet')).toBeVisible();
});

test('a suspended tenant is resumed from its page', async ({ page, api }) => {
  await page.goto(`/tenants/${data.OTHER_TENANT.tenantId}`);
  await page.getByRole('button', { name: 'Resume' }).click();
  await expect(page.getByText(`Resumed ${data.OTHER_TENANT.slug}`)).toBeVisible();
  expect(api.requestsTo('POST', `/api/platform/tenants/${data.OTHER_TENANT.tenantId}/resume`)).toHaveLength(1);
});

test.describe('an operator who may only read tenants', () => {
  test.use({ grants: ['platform:tenants:read'] });

  test('sees the page, and no control that would change the tenant', async ({ page }) => {
    await page.goto(`/tenants/${data.TENANT.tenantId}`);
    await expect(page.getByRole('heading', { name: 'Data usage' })).toBeVisible();
    await expect(page.getByText('owner@acme.example')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Measure now' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Change limit' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Suspend' })).toHaveCount(0);
  });
});

test('an unknown tenant is not a blank page', async ({ page }) => {
  await page.goto('/tenants/aaaaaaaa-0000-0000-0000-00000000ffff');
  await expect(page.getByText('No such tenant')).toBeVisible();
});
