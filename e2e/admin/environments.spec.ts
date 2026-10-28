import { test, expect } from '../fixtures/test';
import { TENANT, OTHER_TENANT } from '../fixtures/data';

/**
 * Environments (ADR 0028): adding them on Settings → Environments, the pipeline, and the pill beside
 * the workspace switcher that moves between them keeping the section.
 */

const DEV = 'aaaaaaaa-0000-0000-0000-0000000000d1';
const ENABLED = {
  enabled: true,
  workspaceId: TENANT.tenantId,
  environments: [
    { id: DEV, name: 'development', order: -1, purpose: 'stage', isProduction: false, promotesFrom: [], isMember: true },
    { id: TENANT.tenantId, name: 'production', order: 0, purpose: 'stage', isProduction: true, promotesFrom: [DEV], isMember: true },
  ],
};

test('a workspace with production alone adds its first environment from settings', async ({ page, api }) => {
  api.on('POST', '/api/admin/environments', ({ body }) => ({ ...ENABLED.environments[0], name: (body as { name: string }).name }));
  await page.goto('/settings/environments');
  await expect(page.getByRole('heading', { name: 'Environments', level: 1 })).toBeVisible();
  // No pill while the workspace has production alone.
  await expect(page.getByRole('button', { name: /Switch environment/ })).toHaveCount(0);

  await page.getByLabel('New environment').fill('development');
  await page.getByRole('button', { name: 'Add environment' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments').map((c) => c.body)).toEqual([{ name: 'development' }]);
});

test('the pipeline lists environments in order and adds one', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('POST', '/api/admin/environments', ({ body }) => ({ ...ENABLED.environments[0], name: (body as { name: string }).name }));
  await page.goto('/settings/environments');

  const names = page.getByRole('heading', { level: 2, name: /^(development|production)$/ });
  await expect(names).toHaveText(['development', 'production']);
  await expect(page.getByRole('checkbox', { name: 'development' })).toBeChecked();

  // Production is locked to the pipeline in one go.
  await page.getByRole('button', { name: 'Lock to pipeline' }).last().click();
  await expect.poll(() => api.requestsTo('PUT', `/api/admin/environments/${TENANT.tenantId}/policies`).map((r) => (r.body as { mode: string }[]).map((p) => p.mode)))
    .toEqual([['Managed', 'Managed', 'Managed', 'Managed', 'Managed', 'Managed']]);

  await page.getByLabel('New environment').fill('test');
  await page.getByRole('button', { name: 'Add environment' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments').map((c) => c.body)).toEqual([{ name: 'test' }]);
});

test('the pill switches environment and keeps the section', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('GET', '/api/admin/me/tenants', [TENANT, OTHER_TENANT]);
  await page.goto('/media');

  // Workspaces only in the workspace switcher.
  await page.getByRole('button', { name: 'Acme Studio' }).first().click();
  await expect(page.getByRole('dialog').getByRole('button')).toHaveText(['Acme Studio', 'Globex', 'Create tenant']);
  await page.keyboard.press('Escape');

  await page.getByRole('button', { name: 'Environment: production. Switch environment' }).click();
  await page.getByRole('button', { name: 'development' }).click();
  await expect(page).toHaveURL(/\/media$/);
  expect(await page.evaluate((slug) => localStorage.getItem(`dcms.environment:${slug}`), TENANT.slug)).toBe('development');
  // Every request from then on names it.
  await expect.poll(() => api.requestsTo('GET', '/api/admin/media').at(-1)?.headers['x-dcms-environment']).toBe('development');
});

test('a duplicate copies the data ticked, and warns about personal data', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('POST', '/api/admin/environments', ({ body }) => ({ ...ENABLED.environments[0], name: (body as { name: string }).name }));
  await page.goto('/settings/environments');

  await page.getByLabel('New environment').fill('staging');
  await page.getByRole('checkbox', { name: 'Dynamic Apps records' }).click();
  await page.getByRole('checkbox', { name: /Form submissions/ }).click();
  await expect(page.getByText(/Personal data copied here is held twice/)).toBeVisible();
  await page.getByRole('button', { name: 'Add environment' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments').map((c) => c.body))
    .toEqual([{ name: 'staging', copy: ['dynamic-apps-records', 'form-submissions'] }]);
});
