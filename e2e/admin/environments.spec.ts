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

  await page.getByRole('button', { name: 'New environment' }).last().click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('radio', { name: /^Stage/ }).click();
  await dialog.getByRole('button', { name: 'Next' }).click();
  await dialog.getByLabel('Name').fill('development');
  await dialog.getByRole('button', { name: 'Next' }).click();
  await dialog.getByRole('button', { name: 'Create development' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments').map((c) => c.body))
    .toEqual([{ name: 'development', from: TENANT.tenantId }]);
});

test('the pipeline lists environments in order and adds one', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('POST', '/api/admin/environments', ({ body }) => ({ ...ENABLED.environments[0], name: (body as { name: string }).name }));
  await page.goto('/settings/environments');

  // The pipeline, in the order configuration travels it.
  const tiles = page.getByRole('list', { name: 'Pipeline' }).getByRole('button');
  await expect(tiles.nth(0)).toContainText('development');
  await expect(tiles.nth(1)).toContainText('production');
  await expect(tiles.nth(1)).toContainText('Takes deployments from development');

  // Production is locked to the pipeline in one go, on its own page.
  await tiles.nth(1).click();
  await expect(page).toHaveURL(/env=production/);
  await page.getByRole('tab', { name: 'Locks' }).click();
  await page.getByRole('button', { name: 'Lock to pipeline' }).click();
  await expect.poll(() => api.requestsTo('PUT', `/api/admin/environments/${TENANT.tenantId}/policies`).map((r) => (r.body as { mode: string }[]).map((p) => p.mode)))
    .toEqual([['Managed', 'Managed', 'Managed', 'Managed', 'Managed', 'Managed']]);
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

  await page.getByRole('button', { name: 'New environment' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('radio', { name: /^Stage/ }).click();
  await dialog.getByRole('button', { name: 'Next' }).click();
  await dialog.getByLabel('Name').fill('staging');
  await dialog.getByRole('button', { name: 'Next' }).click();
  await dialog.getByRole('checkbox', { name: 'Dynamic Apps records' }).click();
  await dialog.getByRole('checkbox', { name: /Form submissions/ }).click();
  await expect(dialog.getByText(/Personal data copied here is held twice/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Create staging' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments').map((c) => c.body))
    .toEqual([{ name: 'staging', from: TENANT.tenantId, copy: ['dynamic-apps-records', 'form-submissions'] }]);
});

test('a feature environment branches from the one chosen and can go once merged', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('POST', '/api/admin/environments', ({ body }) => ({ ...ENABLED.environments[0], name: (body as { name: string }).name }));
  await page.goto('/settings/environments');

  await page.getByRole('button', { name: 'New environment' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('radio', { name: /^Feature/ }).click();
  await dialog.getByRole('button', { name: 'Next' }).click();
  await dialog.getByLabel('Name').fill('development-feature1');
  await expect(dialog.getByRole('radio', { name: /^development/ })).toHaveAttribute('aria-checked', 'true');
  await expect(dialog.getByRole('switch')).toBeChecked();
  await dialog.getByRole('button', { name: 'Next' }).click();
  await dialog.getByRole('button', { name: 'Create development-feature1' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments').map((c) => c.body))
    .toEqual([{ name: 'development-feature1', from: DEV, feature: true, deleteWhenMerged: true }]);
});

test('an environment uses a service environment’s tables for what is granted', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('GET', '/api/admin/environments/:id/offers', [
    { slug: 'crm', name: 'CRM', pluginId: 'dynamic-apps', operations: [], tables: ['companies', 'deals'] },
  ]);
  api.on('POST', '/api/admin/environments/:id/links', { id: 'l1' });
  await page.goto('/settings/environments?env=development&tab=links');

  await page.getByRole('button', { name: 'Use tables' }).click();
  const dialog = page.getByRole('dialog');
  // production is the only other environment, so it is already chosen.
  await dialog.getByRole('radio', { name: /^CRM/ }).click();
  await dialog.getByRole('checkbox', { name: 'companies:read' }).click();
  await dialog.getByRole('checkbox', { name: 'deals:create' }).click();
  await dialog.getByRole('button', { name: 'Allow these tables' }).click();
  await expect.poll(() => api.requestsTo('POST', `/api/admin/environments/${DEV}/links`).map((r) => r.body))
    .toEqual([{ kind: 'tables', targetEnvironmentId: TENANT.tenantId, targetKey: 'crm', grants: ['companies:read', 'deals:create'] }]);
});

test('an environment uses another’s app operations, picked from what it offers', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('GET', '/api/admin/environments/:id/offers', [
    { slug: 'invoices', name: 'Invoices', pluginId: 'blog', tables: [], operations: [
      { grant: 'blog.posts/Get', risk: 'read', description: 'One published post by slug.' },
      { grant: 'blog.posts/List', risk: 'read', description: 'Published posts, newest first.' },
    ] },
  ]);
  api.on('POST', '/api/admin/environments/:id/links', { id: 'l1' });
  await page.goto('/settings/environments?env=development&tab=links');

  await page.getByRole('button', { name: 'Use operations' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('radio', { name: /^Invoices/ }).click();
  await dialog.getByRole('checkbox').first().click();
  await dialog.getByRole('button', { name: 'Allow these operations' }).click();
  await expect.poll(() => api.requestsTo('POST', `/api/admin/environments/${DEV}/links`).map((r) => r.body))
    .toEqual([{ kind: 'contracts', targetEnvironmentId: TENANT.tenantId, targetKey: 'invoices', grants: ['blog.posts/Get'] }]);
});

test('an app link offers the other environment’s apps, and rules out an address this one has', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('GET', `/api/admin/environments/${TENANT.tenantId}/offers`, [
    { slug: 'news', name: 'News', pluginId: 'blog', tables: [], operations: [] },
    { slug: 'leads', name: 'Leads', pluginId: 'forms', tables: [], operations: [] },
  ]);
  api.on('GET', `/api/admin/environments/${DEV}/offers`, [{ slug: 'news', name: 'Own news', pluginId: 'blog', tables: [], operations: [] }]);
  api.on('POST', '/api/admin/environments/:id/links', { id: 'l1' });
  await page.goto('/settings/environments?env=development&tab=links');

  await page.getByRole('button', { name: 'Use an app' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('radio', { name: /^News/ })).toBeDisabled();
  await expect(dialog.getByText('This environment has its own app at /api/news')).toBeVisible();
  await dialog.getByRole('radio', { name: /^Leads/ }).click();
  await dialog.getByRole('radio', { name: /^Read and write/ }).click();
  await dialog.getByRole('button', { name: 'Use this app' }).click();
  await expect.poll(() => api.requestsTo('POST', `/api/admin/environments/${DEV}/links`).map((r) => r.body))
    .toEqual([{ kind: 'app', targetEnvironmentId: TENANT.tenantId, targetKey: 'leads', access: 'readWrite' }]);
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
  await page.goto('/settings/environments?env=production&tab=locks');

  await page.getByLabel('Kind of the exception').click();
  await page.getByRole('option', { name: 'Published content' }).click();
  await page.getByLabel('Item', { exact: true }).fill('news/post/legal');
  await page.getByRole('button', { name: 'Add exception' }).click();
  await expect.poll(() => api.requestsTo('PUT', `/api/admin/environments/${TENANT.tenantId}/policies`).map((r) => r.body)).toEqual([[
    { kind: 'content', itemKey: 'news/post/old', mode: 'Local' },
    { kind: 'content', itemKey: 'news/post/legal', mode: 'Local' },
  ]]);

  await page.getByRole('button', { name: 'Remove the exception for news/post/old' }).click();
  await expect.poll(() => api.requestsTo('PUT', `/api/admin/environments/${TENANT.tenantId}/policies`).at(-1)?.body).toEqual([]);
});

test('a screen this environment leaves to deployments says where to change it', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('GET', `/api/admin/environments/${TENANT.tenantId}/policies`, [
    { kind: 'roles', itemKey: null, mode: 'Managed' },
    { kind: 'roles', itemKey: 'Auditors', mode: 'Local' },
  ]);
  api.on('GET', '/api/admin/roles', [
    { id: 'r1', name: 'Editors', isSystem: false, permissions: ['content:read'], memberCount: 0 },
    { id: 'r2', name: 'Auditors', isSystem: false, permissions: ['audit:read'], memberCount: 0 },
  ]);
  await page.goto('/settings/roles');
  await expect(page.getByText('Managed by deployments')).toBeVisible();
  await expect(page.getByText(/takes this from development/)).toBeVisible();

  // What would be refused is not offered; production's own role still is.
  const row = (name: string) => page.getByRole('row').filter({ hasText: name });
  await expect(row('Editors').getByRole('button', { name: 'Edit' })).toBeDisabled();
  await expect(row('Editors').getByRole('button', { name: 'Delete' })).toBeDisabled();
  await expect(row('Auditors').getByRole('button', { name: 'Edit' })).toBeEnabled();
  // An exception keeps one name local, so a role can still be made here -- under that name.
  await expect(page.getByRole('button', { name: 'Create role' }).first()).toBeEnabled();
});

test('content this environment takes from deployments is not edited here', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments', ENABLED);
  api.on('GET', `/api/admin/environments/${TENANT.tenantId}/policies`, [{ kind: 'content', itemKey: null, mode: 'Managed' }]);
  await page.goto('/content');
  await page.getByRole('button', { name: 'New item' }).first().click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('Managed by deployments')).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
  await expect(dialog.getByRole('button', { name: 'Save and publish' })).toBeDisabled();
});

test('an environment being set up shows each step and cannot be opened yet', async ({ page, api }) => {
  const QA = 'aaaaaaaa-0000-0000-0000-0000000000a1';
  api.on('GET', '/api/admin/environments', {
    ...ENABLED,
    environments: [
      { id: QA, name: 'qa', order: -2, purpose: 'stage', isProduction: false, promotesFrom: [], isMember: true, ready: false, setup: [
        { key: 'kind:roles', state: 'done', done: 5, total: 5, item: null },
        { key: 'kind:sites', state: 'running', done: 1, total: 3, item: 'Store' },
        { key: 'data:dynamic-apps-records', state: 'waiting', done: 0, total: null, item: null },
      ] },
      ...ENABLED.environments.map((e) => ({ ...e, ready: true, setup: null })),
    ],
  });
  await page.goto('/settings/environments?env=qa');
  await expect(page.getByText('Being set up: 1 of 3 steps')).toBeVisible();
  await expect(page.getByText('Store (2 of 3)')).toBeVisible();
  await expect(page.getByText('5 copied')).toBeVisible();
  // Its settings wait until it is ready.
  await expect(page.getByRole('tab', { name: 'Locks' })).toHaveCount(0);

  // The switcher lists it, but not as somewhere to go yet.
  await page.getByRole('button', { name: 'Environment: production. Switch environment' }).click();
  await expect(page.getByRole('button', { name: /^qa/ })).toBeDisabled();
});
