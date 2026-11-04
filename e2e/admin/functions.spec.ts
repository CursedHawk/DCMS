import { test, expect } from '../fixtures/test';
import * as data from '../fixtures/data';

/**
 * A Functions app's screens (ADR 0029) in the real console against a mocked server: a build is
 * deployed only after a confirmation, an API key is shown once, a secret is written and never
 * read back, and the run history opens a run's input, result and log.
 */

test.use({
  grants: [
    ...data.ALL_TENANT_PERMISSIONS,
    'plugin:functions:read', 'plugin:functions:edit', 'plugin:functions:deploy',
    'plugin:functions:logs', 'plugin:functions:keys', 'plugin:functions:secrets',
  ],
});

const INSTANCE = {
  id: 'cccccccc-0000-0000-0000-0000000000f1', pluginId: 'functions', slug: 'fn', name: 'Automations',
  description: 'Functions', enabled: true, config: '{}', aiToolsEnabled: true,
};

const screen = (id: string, title: string, permission: string, section = 'work') =>
  ({ id, title, titles: {}, description: null, scope: 'instance', icon: 'Code', permission: `plugin:functions:${permission}`, section, allowed: true });

const READY = {
  id: 'dddddddd-0000-0000-0000-000000000002', number: 2, commitSha: 'b2c3d4e5f6a7', status: 'Ready', error: null,
  functions: ['hello', 'orders'], createdAt: '2026-11-04T10:00:00Z', activatedAt: null, activatedBy: null,
};
const LIVE = {
  id: 'dddddddd-0000-0000-0000-000000000001', number: 1, commitSha: 'a1b2c3d4e5f6', status: 'Active', error: null,
  functions: ['hello'], createdAt: '2026-11-03T10:00:00Z', activatedAt: '2026-11-03T10:05:00Z', activatedBy: 'owner',
};
const RUN = {
  id: 'eeeeeeee-0000-0000-0000-000000000001', function: 'hello', deployment: 1, trigger: 'http', status: 'Succeeded',
  attempts: 1, error: null, durationMs: 42, createdAt: '2026-11-04T09:00:00Z', finishedAt: '2026-11-04T09:00:01Z',
};

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/plugin-ui', [
      ...data.PLUGIN_UI,
      {
        pluginId: 'functions', name: 'Functions', icon: 'SquareFunction', source: 'builtin',
        module: { kind: 'builtin', key: 'Dcms.Plugins.Functions' },
        screens: [
          screen('deployments', 'Deployments', 'read'), screen('runs', 'Runs', 'logs'),
          screen('keys', 'API keys', 'keys', 'admin'), screen('secrets', 'Secrets', 'secrets', 'admin'),
        ],
        instances: [{ id: INSTANCE.id, slug: 'fn', name: 'Automations', enabled: true }],
      },
    ])
    .on('GET', '/api/admin/plugins/instances', [...data.PLUGIN_INSTANCES, INSTANCE])
    .on('GET', '/api/admin/plugins/catalog', [
      ...data.PLUGIN_CATALOG,
      {
        id: 'functions', name: 'Functions', version: '1.0.0', description: 'TypeScript functions.', allowMultipleInstances: true,
        configJsonSchema: '{}', permissions: [], dependencies: [], publicConfigKeys: [], contentTypes: [], provides: [], consumes: [],
      },
    ]);
});

test('a build goes live only once its deployer confirms', async ({ page, api }) => {
  api
    .on('GET', '/api/admin/plugins/fn/_functions/deployments', [READY, LIVE])
    .on('POST', `/api/admin/plugins/fn/_functions/deployments/${READY.id}/activate`, { ...READY, status: 'Active' });
  await page.goto('/apps/fn/deployments');

  await expect(page.getByText('b2c3d4e').first()).toBeVisible();
  await page.getByRole('button', { name: 'Deploy', exact: true }).first().click();
  await expect(page.getByRole('alertdialog').or(page.getByRole('dialog'))).toContainText('Deploy build 2?');
  expect(api.requestsTo('POST', `/api/admin/plugins/fn/_functions/deployments/${READY.id}/activate`)).toHaveLength(0);
  await page.getByRole('alertdialog').or(page.getByRole('dialog')).getByRole('button', { name: 'Deploy' }).click();
  await expect.poll(() => api.requestsTo('POST', `/api/admin/plugins/fn/_functions/deployments/${READY.id}/activate`).length).toBe(1);
});

test('a new API key is shown once, with the functions it may call', async ({ page, api }) => {
  api
    .on('GET', '/api/admin/plugins/fn/_functions/keys', [])
    .on('POST', '/api/admin/plugins/fn/_functions/keys', {
      key: { id: 'k1', name: 'Shop', prefix: 'dfk_abcd1234', functions: ['orders'], createdAt: '2026-11-04T11:00:00Z' },
      secret: 'dfk_abcd1234_secret-value',
    });
  await page.goto('/apps/fn/keys');

  await page.getByRole('button', { name: 'New key' }).click();
  await page.getByLabel('Name').fill('Shop');
  await page.getByLabel('Functions').fill('orders');
  await page.getByRole('button', { name: 'Create' }).click();

  await expect(page.getByRole('dialog')).toContainText('dfk_abcd1234_secret-value');
  await expect(page.getByRole('dialog')).toContainText('shown this once');
  expect(api.requestsTo('POST', '/api/admin/plugins/fn/_functions/keys')[0]!.body).toEqual({ name: 'Shop', functions: ['orders'] });
});

test('a secret is written by name and never shown back', async ({ page, api }) => {
  api
    .on('GET', '/api/admin/plugins/fn/_functions/secrets', ['STRIPE_KEY'])
    .on('PUT', '/api/admin/plugins/fn/_functions/secrets/MAIL_TOKEN', { status: 204 });
  await page.goto('/apps/fn/secrets');

  await expect(page.getByText('STRIPE_KEY')).toBeVisible();
  await page.getByLabel('Name').fill('mail_token');
  await page.getByLabel('Value').fill('s3cret');
  await page.getByRole('button', { name: 'Save secret' }).click();

  await expect.poll(() => api.requestsTo('PUT', '/api/admin/plugins/fn/_functions/secrets/MAIL_TOKEN').length).toBe(1);
  expect(api.requestsTo('PUT', '/api/admin/plugins/fn/_functions/secrets/MAIL_TOKEN')[0]!.body).toEqual({ value: 's3cret' });
  await expect(page.getByText('s3cret')).toHaveCount(0);
});

test('a run opens with its input, result and log', async ({ page, api }) => {
  api
    .on('GET', '/api/admin/plugins/fn/_functions/runs', { items: [RUN], page: 1, more: false })
    .on('GET', `/api/admin/plugins/fn/_functions/runs/${RUN.id}`, {
      run: RUN, input: { name: 'Ada' }, trigger: { method: 'GET' }, result: { message: 'Hello, Ada!' },
      logs: '[info] hello Ada', calls: 0, correlationId: 'ffffffff-0000-0000-0000-000000000001', depth: 0,
    });
  await page.goto('/apps/fn/runs');

  await page.getByText('hello', { exact: true }).first().click();
  await expect(page.getByText('Hello, Ada!')).toBeVisible();
  await expect(page.getByText('[info] hello Ada')).toBeVisible();
});
