import { platformTest as test, expect, data } from '../fixtures/test';

/**
 * The per-tenant storage cap, set from the tenant's page. admin-api does the enforcing, so what
 * this proves is the contract and the GB → bytes conversion, not the refusal itself.
 */
test('an operator raises a tenant storage limit', async ({ page, api }) => {
  await page.goto(`/tenants/${data.TENANT.tenantId}`);
  await page.getByRole('button', { name: 'Change limit' }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog.getByLabel('Limit in GB')).toHaveValue('5');
  await dialog.getByLabel('Limit in GB').fill('12.5');
  await dialog.getByRole('button', { name: 'Save limit' }).click();

  await expect(page.getByText(`${data.TENANT.slug} can now store 12.5 GB`)).toBeVisible();
  const put = api.requestsTo('PUT', `/api/platform/tenants/${data.TENANT.tenantId}/storage-quota`).at(-1);
  expect(put?.body).toEqual({ quotaBytes: 12.5 * 1024 ** 3 });
});

test("the tenant page's actions work on a phone", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });

  await page.goto(`/tenants/${data.TENANT.tenantId}`);
  await page.getByRole('button', { name: 'Change limit' }).click();
  await expect(page.getByRole('dialog').getByLabel('Limit in GB')).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();

  await page.getByRole('button', { name: 'Suspend' }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
});
