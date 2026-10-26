import { test, expect } from '../fixtures/test';
import { TENANT } from '../fixtures/data';

/**
 * Deployments (ADR 0026): this environment's unpublished changes cut into a migration; a pending
 * deployment reviewed change by change, a conflict decided before Deploy is possible; and the
 * latest migration rolled back from the history.
 */

const DEV = 'aaaaaaaa-0000-0000-0000-0000000000d1';
const ENVIRONMENTS = {
  enabled: true,
  workspaceId: TENANT.tenantId,
  environments: [
    { tenantId: DEV, slug: 'acme-development', name: 'development', order: -1, isRoot: false, promotesFrom: [], status: 'Active', isMember: true },
    { tenantId: TENANT.tenantId, slug: TENANT.slug, name: 'production', order: 0, isRoot: true, promotesFrom: [DEV], status: 'Active', isMember: true },
  ],
};
const change = (id: string, outcome: string | null, before: unknown, after: unknown, current?: unknown) => ({
  id, kind: id.split('/')[0], key: id.split('/')[1], label: id.split('/')[1], op: before == null ? 'add' : 'update',
  before, after, current, outcome,
});
const migration = (migrationId: string, name: string, changes: unknown[]) => ({
  migrationId, name, description: null, origin: DEV, originName: 'development', authorEmail: 'dev@acme.test',
  createdAt: '2026-10-26T10:00:00Z', appliedAt: '2026-10-26T10:00:00Z', rolledBackAt: null, resolutions: {}, changes,
});

test.beforeEach(({ api }) => {
  api.on('GET', '/api/admin/environments', ENVIRONMENTS);
  api.on('GET', '/api/admin/environments/pending', []);
});

test('unpublished changes become a named migration', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments/changes', [change('roles/Editors', null, null, { System: false, Permissions: ['content:read'] })]);
  api.on('POST', '/api/admin/environments/migrations', migration('m1', 'Add editors', []));
  await page.goto('/deployments');

  await expect(page.getByRole('heading', { name: 'Deployments', level: 1 })).toBeVisible();
  await expect(page.getByText('Editors')).toBeVisible();
  await page.getByLabel('Migration name').fill('Add editors');
  await page.getByRole('button', { name: 'Create migration of 1 change' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments/migrations').map((r) => r.body))
    .toEqual([{ name: 'Add editors' }]);
});

test('a conflict must be decided before the deployment can run', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments/pending', [{
    migration: migration('m2', 'Editors publish', []),
    changes: [change('roles/Editors', 'conflict', { Permissions: ['content:read'] }, { Permissions: ['content:publish'] }, { Permissions: ['media:read'] })],
  }]);
  api.on('POST', '/api/admin/environments/pending/apply', { jobId: 'j1' });
  await page.goto('/deployments');

  await expect(page.getByRole('heading', { name: 'Waiting to deploy from development' })).toBeVisible();
  const deploy = page.getByRole('button', { name: 'Deploy', exact: true });
  await expect(deploy).toBeDisabled();
  // The conflict opens with what is here now beside the before and after.
  await expect(page.getByText('In this environment now')).toBeVisible();

  await page.getByLabel('Overwrite with the deployed version').check();
  await deploy.click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments/pending/apply').map((r) => r.body))
    .toEqual([{ from: DEV, upTo: 'm2', resolutions: { 'm2/roles/Editors': 'overwrite' } }]);
});

test('the latest migration rolls back after its plan is shown', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments/migrations', [migration('m1', 'Add editors', [change('roles/Editors', null, null, { Permissions: [] })])]);
  api.on('GET', '/api/admin/environments/migrations/:id/rollback', {
    migration: migration('m1', 'Add editors', []),
    changes: [change('roles/Editors', 'apply', { Permissions: [] }, null, { Permissions: [] })],
  });
  api.on('POST', '/api/admin/environments/migrations/:id/rollback', { jobId: 'j2' });
  await page.goto('/deployments');

  await page.getByRole('button', { name: 'Roll back' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('Roll back Add editors?')).toBeVisible();
  await dialog.getByRole('button', { name: 'Roll back' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments/migrations/m1/rollback').length).toBe(1);
});
