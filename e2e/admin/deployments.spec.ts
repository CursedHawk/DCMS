import { test, expect } from '../fixtures/test';
import { TENANT } from '../fixtures/data';

/**
 * Deployments (ADR 0026, 0028): this environment's unpublished changes cut into a migration; a pending
 * deployment reviewed change by change, a conflict decided before Deploy is possible; and the
 * latest migration rolled back from the history.
 */

const DEV = 'aaaaaaaa-0000-0000-0000-0000000000d1';
const ENVIRONMENTS = {
  enabled: true,
  workspaceId: TENANT.tenantId,
  environments: [
    { id: DEV, name: 'development', order: -1, purpose: 'stage', isProduction: false, promotesFrom: [], isMember: true },
    { id: TENANT.tenantId, name: 'production', order: 0, purpose: 'stage', isProduction: true, promotesFrom: [DEV], isMember: true },
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

test('a change left unticked stays out of the migration', async ({ page, api }) => {
  api.on('GET', '/api/admin/environments/changes', [
    change('roles/Editors', null, null, { System: false, Permissions: ['content:read'] }),
    change('roles/Reviewers', null, null, { System: false, Permissions: ['content:read'] }),
  ]);
  api.on('POST', '/api/admin/environments/migrations', migration('m1', 'Editors', []));
  await page.goto('/deployments');

  await page.getByRole('checkbox', { name: 'Include Reviewers in the migration' }).click();
  await page.getByLabel('Migration name').fill('Editors');
  await page.getByRole('button', { name: 'Create migration of 1 change' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments/migrations').map((r) => r.body))
    .toEqual([{ name: 'Editors', changes: ['roles/Editors'] }]);
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

test('a conflict can be decided path by path, keeping what merged cleanly', async ({ page, api }) => {
  const app = (label: string, color: string) => ({ tables: [{ id: 't1', label, color }] });
  api.on('GET', '/api/admin/environments/pending', [{
    migration: migration('m3', 'Deal colours', []),
    changes: [{
      ...change('dynamic-apps/crm', 'conflict', app('Deals', 'red'), app('Their deals', 'blue'), app('Our deals', 'red')),
      merged: app('Their deals', 'blue'),
      conflicts: [{ path: '/tables/t1/label', ours: 'Our deals', theirs: 'Their deals' }],
    }],
  }]);
  api.on('POST', '/api/admin/environments/pending/apply', { jobId: 'j3' });
  await page.goto('/deployments');

  const deploy = page.getByRole('button', { name: 'Deploy', exact: true });
  await expect(deploy).toBeDisabled();
  await expect(page.getByText('/tables/t1/label').first()).toBeVisible();
  await page.getByLabel("Keep this environment's", { exact: true }).check();
  await deploy.click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/environments/pending/apply').map((r) => r.body))
    .toEqual([{ from: DEV, upTo: 'm3', resolutions: { 'm3/dynamic-apps/crm#/tables/t1/label': 'ours' } }]);
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
