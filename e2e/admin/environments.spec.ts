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

test('a feature environment branches from the one chosen and can go once merged', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('POST', '/api/admin/environments', ({ body }) => ({ ...ENABLED.environments[0], name: (body as { name: string }).name }));
  await page.goto('/settings/environments');

  await page.getByLabel('New environment').fill('development-feature1');
  await page.getByLabel('Copy from').click();
  await page.getByRole('option', { name: 'development' }).click();
  await page.getByRole('checkbox', { name: 'Feature environment' }).click();
  await expect(page.getByRole('checkbox', { name: 'Delete once merged' })).toBeChecked();
  await page.getByRole('button', { name: 'Add environment' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments').map((c) => c.body))
    .toEqual([{ name: 'development-feature1', from: DEV, feature: true, deleteWhenMerged: true }]);
});

test('an environment uses a service environment’s tables for what is granted', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('POST', '/api/admin/environments/:id/links', { id: 'l1' });
  await page.goto('/settings/environments');

  // development is listed first; its form is the one around its grants.
  const form = page.getByLabel('Grants').first().locator('xpath=ancestor::form[1]');
  await form.getByLabel('App address there').fill('crm');
  await form.getByLabel('Grants').fill('companies:read\ndeals:create');
  await form.getByRole('button', { name: 'Use from there' }).click();
  await expect.poll(() => api.requestsTo('POST', `/api/admin/environments/${DEV}/links`).map((r) => r.body))
    .toEqual([{ kind: 'tables', targetEnvironmentId: TENANT.tenantId, targetKey: 'crm', grants: ['companies:read', 'deals:create'] }]);
});

test('the palette switches environment and keeps the page', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  await page.goto('/settings/roles');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

  await page.keyboard.press('ControlOrMeta+k');
  const palette = page.getByRole('dialog');
  await palette.getByRole('combobox').fill('development');
  // Only the others: production is where you are.
  await expect(palette.getByRole('option', { name: 'Switch to production' })).toHaveCount(0);
  await palette.getByRole('option', { name: 'Switch to development' }).click();
  await expect.poll(() => page.evaluate((slug) => localStorage.getItem(`dcms.environment:${slug}`), TENANT.slug)).toBe('development');
  await expect(page).toHaveURL(/\/settings\/roles$/);
});

test('one item is owned otherwise than its kind', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('GET', `/api/admin/environments/${TENANT.tenantId}/policies`, [{ kind: 'content', itemKey: 'news/post/old', mode: 'Local' }]);
  await page.goto('/settings/environments');

  // production is listed last.
  await page.getByLabel('Kind of the exception').last().click();
  await page.getByRole('option', { name: 'Published content' }).click();
  await page.getByLabel('Item', { exact: true }).last().fill('news/post/legal');
  await page.getByRole('button', { name: 'Add exception' }).last().click();
  await expect.poll(() => api.requestsTo('PUT', `/api/admin/environments/${TENANT.tenantId}/policies`).map((r) => r.body)).toEqual([[
    { kind: 'content', itemKey: 'news/post/old', mode: 'Local' },
    { kind: 'content', itemKey: 'news/post/legal', mode: 'Local' },
  ]]);

  await page.getByRole('button', { name: 'Remove the exception for news/post/old' }).click();
  await expect.poll(() => api.requestsTo('PUT', `/api/admin/environments/${TENANT.tenantId}/policies`).at(-1)?.body).toEqual([]);
});

test('a screen this environment leaves to deployments says where to change it', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('GET', `/api/admin/environments/${TENANT.tenantId}/policies`, [{ kind: 'roles', itemKey: null, mode: 'Managed' }]);
  await page.goto('/settings/roles');
  await expect(page.getByText('Managed by deployments')).toBeVisible();
  await expect(page.getByText(/takes this from development/)).toBeVisible();
});
