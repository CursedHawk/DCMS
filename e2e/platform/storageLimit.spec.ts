import { platformTest as test, expect, data } from '../fixtures/test';

/**
 * The per-tenant storage cap. The console shows used of cap per row and sends a new cap in
 * bytes; admin-api does the enforcing, so what this proves is the contract and the GB → bytes
 * conversion, not the refusal itself.
 */
test('an operator raises a tenant storage limit', async ({ page, api }) => {
  api.on('PUT', '/api/platform/tenants/:id/storage-quota', {});

  await page.goto('/tenants');
  await page
    .getByRole('row')
    .filter({ hasText: data.TENANT.slug })
    .getByRole('button', { name: /change storage limit/ })
    .click();

  const dialog = page.getByRole('dialog');
  await expect(dialog.getByLabel('Limit in GB')).toHaveValue('5');
  await dialog.getByLabel('Limit in GB').fill('12.5');
  await dialog.getByRole('button', { name: 'Save limit' }).click();

  await expect(page.getByText(`${data.TENANT.slug} can now store 12.5 GB`)).toBeVisible();
  const put = api.requestsTo('PUT', `/api/platform/tenants/${data.TENANT.tenantId}/storage-quota`).at(-1);
  expect(put?.body).toEqual({ quotaBytes: 12.5 * 1024 ** 3 });
});
