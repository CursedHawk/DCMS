import { test, expect } from '../fixtures/test';
import { TENANT, OTHER_TENANT } from '../fixtures/data';

/**
 * Environments (ADR 0026): turning them on, the pipeline on Settings → Environments, and the
 * pill beside the workspace switcher that moves between them keeping the section.
 */

const DEV = { tenantId: 'aaaaaaaa-0000-0000-0000-0000000000d1', slug: 'acme-development', name: 'Acme Studio' };
const env = (t: typeof TENANT, environment: string, environmentOrder: number) => ({
  ...t,
  workspaceId: TENANT.tenantId,
  environment,
  environmentOrder,
});
const ENABLED = {
  enabled: true,
  workspaceId: TENANT.tenantId,
  environments: [
    { tenantId: DEV.tenantId, slug: DEV.slug, name: 'development', order: -1, isRoot: false, promotesFrom: [], status: 'Active', isMember: true },
    { tenantId: TENANT.tenantId, slug: TENANT.slug, name: 'production', order: 0, isRoot: true, promotesFrom: [DEV.tenantId], status: 'Active', isMember: true },
  ],
};

test('a workspace turns environments on from settings', async ({ page, api }) => {
  await page.goto('/settings/environments');
  await expect(page.getByRole('heading', { name: 'Environments', level: 1 })).toBeVisible();
  // No pill while the workspace has a single environment.
  await expect(page.getByRole('button', { name: /Switch environment/ })).toHaveCount(0);

  await page.getByRole('button', { name: 'Turn on environments' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments/enable').length).toBe(1);
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
  api.on('GET', '/api/admin/me/tenants', [env(TENANT, 'production', 0), env(DEV, 'development', -1), OTHER_TENANT]);
  await page.goto('/media');

  // One workspace entry for both environments.
  await page.getByRole('button', { name: 'Acme Studio' }).first().click();
  await expect(page.getByRole('dialog').getByRole('button')).toHaveText(['Acme Studio', 'Globex', 'Create tenant']);
  await page.keyboard.press('Escape');

  await page.getByRole('button', { name: 'Environment: production. Switch environment' }).click();
  await page.getByRole('button', { name: 'development' }).click();
  await expect(page).toHaveURL(/\/media$/);
  expect(await page.evaluate(() => localStorage.getItem('dcms.tenant'))).toBe(DEV.slug);
});
