import { test, expect } from '../fixtures/test';
import * as data from '../fixtures/data';

/**
 * The Dynamic Apps screens (docs/adr/0021) in the real console against a mocked server: the
 * configuration screen edits the draft through change sets that name the hash they were made
 * against, says so when someone else got there first, and publishes only after a review; the
 * records screen edits a record with its version.
 */

test.use({
  grants: [
    ...data.ALL_TENANT_PERMISSIONS,
    'plugin:dynamic-apps:model-read',
    'plugin:dynamic-apps:model-write',
    'plugin:dynamic-apps:publish',
    'plugin:dynamic-apps:data-read',
    'plugin:dynamic-apps:data-write',
    'plugin:dynamic-apps:data-delete',
    'plugin:dynamic-apps:flows-run',
  ],
});

const INSTANCE = {
  id: 'cccccccc-0000-0000-0000-0000000000d1', pluginId: 'dynamic-apps', slug: 'crm', name: 'CRM',
  description: 'Sales CRM', enabled: true, config: '{}', aiToolsEnabled: true,
};

const TABLE_ID = 'aaaaaaaa-0000-0000-0000-000000000001';
const TITLE_ID = 'aaaaaaaa-0000-0000-0000-000000000002';
const AMOUNT_ID = 'aaaaaaaa-0000-0000-0000-000000000003';

const DEALS = {
  id: TABLE_ID, apiName: 'deals', displayName: 'Deal', pluralName: 'Deals', enabled: true, primaryFieldId: TITLE_ID,
  public: { read: 'none', create: false, updateOwn: false, deleteOwn: false },
  fields: [
    { id: TITLE_ID, apiName: 'title', displayName: 'Title', type: 'text', required: true, unique: false, searchable: true, sortable: true,
      filterable: false, deprecated: false, readOnly: false, hiddenFromPublic: false },
    { id: AMOUNT_ID, apiName: 'amount', displayName: 'Amount', type: 'decimal', required: false, unique: false, searchable: false,
      sortable: true, filterable: false, deprecated: false, readOnly: false, hiddenFromPublic: false },
  ],
  indexes: [],
};

const CONFIG = { schemaVersion: 1, settings: {}, tables: [DEALS], relationships: [], choiceSets: [], views: [], flows: [] };

const revision = (number: number, status: string, hash: string) => ({
  id: `bbbbbbbb-0000-0000-0000-00000000000${number}`, number, status, source: 'human', hash,
  createdAt: '2026-10-07T10:00:00Z', updatedAt: '2026-10-07T10:00:00Z',
});

const DRAFT = revision(2, 'draft', 'hash-draft');
const LIVE = { ...revision(1, 'published', 'hash-live'), publishedAt: '2026-10-06T10:00:00Z' };

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/plugin-ui', [
      ...data.PLUGIN_UI,
      {
        pluginId: 'dynamic-apps', name: 'Dynamic Apps', icon: 'DatabaseZap', source: 'builtin',
        module: { kind: 'builtin', key: 'Dcms.Plugins.DynamicApps' },
        screens: [
          { id: 'configuration', title: 'Configuration', titles: { cs: 'Konfigurace' }, description: null, scope: 'instance',
            icon: 'Blocks', permission: 'plugin:dynamic-apps:model-read', nav: null, allowed: true },
          { id: 'records', title: 'Records', titles: { cs: 'Záznamy' }, description: null, scope: 'instance',
            icon: 'Table', permission: 'plugin:dynamic-apps:data-read', nav: null, allowed: true },
        ],
        instances: [{ id: INSTANCE.id, slug: 'crm', name: 'CRM', enabled: true }],
      },
    ])
    .on('GET', '/api/admin/plugins/instances', [...data.PLUGIN_INSTANCES, INSTANCE])
    .on('GET', '/api/admin/plugins/catalog', [
      ...data.PLUGIN_CATALOG,
      {
        id: 'dynamic-apps', name: 'Dynamic Apps', version: '1.0.0', description: 'Your own tables.', allowMultipleInstances: true,
        configJsonSchema: '{}', permissions: [], dependencies: [], publicConfigKeys: [], contentTypes: [], provides: [], consumes: [],
      },
    ])
    .on('GET', '/api/admin/plugins/crm/_model', { draft: DRAFT, published: LIVE, hash: DRAFT.hash })
    .on('GET', '/api/admin/plugins/crm/_model/draft', { revision: DRAFT, config: CONFIG })
    .on('GET', '/api/admin/plugins/crm/_model/published', { revision: LIVE, config: CONFIG })
    .on('GET', '/api/admin/plugins/crm/_model/revisions', { items: [DRAFT, LIVE], total: 2 })
    .on('GET', '/api/admin/plugins/crm/_automation/actions', [])
    .on('GET', '/api/admin/plugins/crm/_automation/runs', { items: [], total: 0, page: 1, pageSize: 50 })
    .on('GET', '/api/admin/plugins/crm/_model/draft/preview', {
      draft: DRAFT, published: LIVE, canPublish: true, hasDestructiveChanges: true, issues: [],
      changes: [
        { op: 'create', resourceType: 'field', path: 'deals.stage', destructive: false },
        { op: 'delete', resourceType: 'field', path: 'deals.legacy', destructive: true },
      ],
    })
    .on('POST', '/api/admin/plugins/crm/_model/draft/publish', { published: true, revision: { ...DRAFT, status: 'published' }, issues: [] });
});

test('a field is added to the draft as a change set naming the hash it was made against', async ({ page, api }) => {
  api.on('POST', '/api/admin/plugins/crm/_model/draft/changes', { draft: DRAFT, changes: [], issues: [] });
  await page.goto('/plugins/crm/configuration');

  await expect(page.getByRole('tab', { name: 'Configuration', selected: true })).toBeVisible();
  await expect(page.getByText('Draft r2')).toBeVisible();
  await expect(page.getByRole('table').getByText('Amount', { exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'Add field' }).click();
  await page.getByLabel('Name', { exact: true }).fill('Close date');
  await expect(page.getByLabel('API name')).toHaveValue('close_date');
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('POST', '/api/admin/plugins/crm/_model/draft/changes').length).toBe(1);
  const body = api.requestsTo('POST', '/api/admin/plugins/crm/_model/draft/changes')[0]!.body as {
    expectedHash: string;
    operations: { op: string; type: string; target: string; value: { apiName: string } }[];
  };
  expect(body.expectedHash).toBe('hash-draft');
  expect(body.operations[0]).toMatchObject({ op: 'create', type: 'field', target: TABLE_ID, value: { apiName: 'close_date' } });
});

test('a stale draft is reported and reloaded rather than overwritten', async ({ page, api }) => {
  api.on('POST', '/api/admin/plugins/crm/_model/draft/changes', ({ route }: { route: import('@playwright/test').Route }) =>
    route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'changed' }) }).then(() => undefined));
  await page.goto('/plugins/crm/configuration');
  const loads = api.requestsTo('GET', '/api/admin/plugins/crm/_model').length;

  await page.getByRole('button', { name: 'Add field' }).click();
  await page.getByLabel('Name', { exact: true }).fill('Stage');
  await page.getByRole('button', { name: 'Save' }).click();

  await expect(page.getByText(/changed elsewhere/)).toBeVisible();
  await expect.poll(() => api.requestsTo('GET', '/api/admin/plugins/crm/_model').length).toBeGreaterThan(loads);
});

test('publishing goes through a review that flags destructive changes', async ({ page, api }) => {
  await page.goto('/plugins/crm/configuration');

  await page.getByRole('button', { name: 'Review & publish' }).click();
  const review = page.getByRole('dialog');
  await expect(review.getByText('deals.legacy')).toBeVisible();
  await expect(review.getByText(/may lose or reject existing data/)).toBeVisible();
  await review.getByRole('button', { name: 'Publish' }).click();

  await expect.poll(() => api.requestsTo('POST', '/api/admin/plugins/crm/_model/draft/publish').length).toBe(1);
  expect(api.requestsTo('POST', '/api/admin/plugins/crm/_model/draft/publish')[0]!.body).toEqual({ expectedHash: 'hash-draft' });
});

test('a record is edited with the version it was read at', async ({ page, api }) => {
  const record = { id: 'dddddddd-0000-0000-0000-000000000001', version: 3, created_at: '2026-10-07T10:00:00Z', updated_at: '2026-10-07T10:00:00Z',
    title: 'Big one', amount: 25000 };
  api
    .on('POST', '/api/admin/plugins/crm/_records/deals/query', { items: [record], total: 1, page: 1, pageSize: 25 })
    .on('PATCH', '/api/admin/plugins/crm/_records/deals/:id', { ...record, version: 4, title: 'Bigger one' });
  await page.goto('/plugins/crm/records');

  await page.getByRole('table').getByText('Big one').click();
  await page.getByLabel('Title').fill('Bigger one');
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PATCH', `/api/admin/plugins/crm/_records/deals/${record.id}`).length).toBe(1);
  expect(api.requestsTo('PATCH', `/api/admin/plugins/crm/_records/deals/${record.id}`)[0]!.body).toEqual({ title: 'Bigger one', version: 3 });
});
