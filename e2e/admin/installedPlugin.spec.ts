import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '../fixtures/test';
import * as data from '../fixtures/data';

/**
 * An installed plugin's admin UI (docs/adr/0019), end to end in the real console: the Guestbook
 * sample's actual build — one ES module and its stylesheet, built with @dcms/plugin-ui's Vite
 * preset — served from "its folder", loaded by URL, rendered with the console's own React and
 * components through the shared-module registry. Nothing in the console names the plugin.
 */

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const dist = resolve(root, 'samples/Dcms.Plugins.Sample.Guestbook/admin/dist');

test.beforeAll(() => {
  // The same build an outside developer runs; done once, and only when it is missing.
  if (!existsSync(resolve(dist, 'index.js'))) {
    execFileSync('pnpm', ['--filter', '@dcms/sample-guestbook-admin', 'build'], { cwd: root, stdio: 'inherit' });
  }
});

// An Owner holds every plugin permission; the default fixture member holds the platform's only.
test.use({
  grants: [
    ...data.ALL_TENANT_PERMISSIONS,
    'plugin:sample-guestbook:read',
    'plugin:sample-guestbook:moderate',
    'plugin:sample-guestbook:export',
  ],
});

const INSTANCE = {
  id: 'cccccccc-0000-0000-0000-0000000000a1', pluginId: 'sample-guestbook', slug: 'guests', name: 'Guests',
  description: 'Our guestbook', enabled: true, config: '{"moderation":"pre","accent":"teal"}',
};

const ENTRY = {
  key: 'e1', title: 'Ada',
  values: { name: 'Ada', message: 'What a fine site, thank you!', status: 'waiting', signedAt: '2026-09-30T10:00:00Z', reply: null },
};

const ENTRIES_SET = {
  id: 'entries', title: 'Entries', description: null, icon: 'NotebookPen', platform: false, canWrite: true,
  columns: [
    { key: 'name', label: 'Name', kind: 'text', sortable: false, primary: true },
    { key: 'message', label: 'Message', kind: 'text', sortable: false, primary: false },
  ],
  itemSchema: null, filters: [], actions: [], searchable: false,
  canCreate: false, canUpdate: false, canDelete: false, canDownload: false, defaultSort: null, defaultDescending: false,
};

test.beforeEach(async ({ api }) => {
  const asset = (file: string, type: string) => ({ route }: { route: import('@playwright/test').Route }) =>
    route.fulfill({ status: 200, contentType: type, body: readFileSync(resolve(dist, file)) }).then(() => undefined);

  api
    .on('GET', '/api/admin/plugin-ui', [
      ...data.PLUGIN_UI,
      {
        pluginId: 'sample-guestbook', name: 'Guestbook (sample)', icon: 'BookOpen', source: 'installed',
        module: {
          kind: 'url',
          url: '/api/admin/plugin-ui/assets/sample-guestbook/index.js',
          css: '/api/admin/plugin-ui/assets/sample-guestbook/index.css',
        },
        screens: [
          {
            id: 'overview', title: 'Guestbooks', titles: { cs: 'Knihy návštěv' }, description: null, scope: 'plugin',
            icon: 'BookOpen', permission: 'plugin:sample-guestbook:read', nav: { group: 'main', order: 60 }, allowed: true,
          },
          {
            id: 'moderation', title: 'Moderation', titles: null, description: null, scope: 'instance',
            icon: 'ShieldCheck', permission: 'plugin:sample-guestbook:moderate', nav: null, allowed: true,
          },
        ],
        instances: [{ id: INSTANCE.id, slug: 'guests', name: 'Guests', enabled: true }],
      },
    ])
    .on('GET', '/api/admin/plugin-ui/assets/sample-guestbook/index.js', asset('index.js', 'text/javascript'))
    .on('GET', '/api/admin/plugin-ui/assets/sample-guestbook/index.css', asset('index.css', 'text/css'))
    .on('GET', '/api/admin/plugins/instances', [...data.PLUGIN_INSTANCES, INSTANCE])
    .on('GET', '/api/admin/plugins/catalog', [
      ...data.PLUGIN_CATALOG,
      {
        id: 'sample-guestbook', name: 'Guestbook (sample)', version: '1.0.0', description: 'A guestbook.',
        allowMultipleInstances: true,
        configJsonSchema: JSON.stringify({
          type: 'object',
          properties: {
            accent: { type: 'string', title: 'Accent colour', format: 'guestbook-accent', enum: ['amber', 'rose', 'teal', 'indigo', 'slate'] },
          },
        }),
        permissions: [], dependencies: [], publicConfigKeys: [], contentTypes: [], provides: [], consumes: [],
      },
    ])
    .on('GET', '/api/admin/guestbook/overview', {
      guestbooks: [{ instanceId: INSTANCE.id, slug: 'guests', name: 'Guests', pending: 2, approved: 5 }],
      blogPostsSeen: 0,
    })
    .on('GET', '/api/admin/plugins/guests/appearance', { title: 'Sign our guestbook', accent: 'teal' })
    .on('GET', '/api/admin/plugins/guests/_data', [ENTRIES_SET])
    .on('GET', '/api/admin/plugins/guests/_data/entries', { rows: [ENTRY], total: 1, page: 1, pageSize: 25 })
    .on('POST', '/api/admin/plugins/guests/entries/:id/approve', { ...ENTRY.values, status: 'Approved' })
    .on('PUT', '/api/admin/plugins/instances/:id', ({ route }) => route.fulfill({ status: 204 }).then(() => undefined));
});

test('an installed plugin’s screen loads from its folder and uses the console’s components', async ({ page }) => {
  await page.goto('/app/sample-guestbook/overview');

  await expect(page.getByRole('heading', { level: 1, name: 'Guestbooks' })).toBeVisible();
  await expect(page.getByText('2 waiting')).toBeVisible();
  // The console's data-set view, embedded by the plugin through host.components.DataSet.
  await expect(page.getByRole('table').getByText('What a fine site, thank you!')).toBeVisible();
  // Its stylesheet came with it.
  await expect(page.locator('link[data-plugin-css]')).toHaveCount(1);
});

test('an instance screen is a tab of the instance page, calling the plugin’s own routes', async ({ page, api }) => {
  await page.goto('/plugins/guests/moderation');

  await expect(page.getByRole('tab', { name: 'Moderation', selected: true })).toBeVisible();
  await expect(page.getByText('What a fine site, thank you!')).toBeVisible();
  await page.getByRole('button', { name: 'Approve' }).click();

  await expect.poll(() => api.requestsTo('POST', '/api/admin/plugins/guests/entries/e1/approve').length).toBe(1);
});

test('a plugin’s config widget replaces the plain field on its settings', async ({ page, api }) => {
  await page.goto('/plugins/guests?tab=settings');

  const accent = page.getByRole('radiogroup', { name: 'Accent colour' });
  await expect(accent.getByRole('radio', { name: 'Teal' })).toHaveAttribute('aria-checked', 'true');
  await accent.getByRole('radio', { name: 'Rose' }).click();
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', `/api/admin/plugins/instances/${INSTANCE.id}`).length).toBe(1);
  const put = api.requestsTo('PUT', `/api/admin/plugins/instances/${INSTANCE.id}`)[0];
  expect(JSON.parse((put.body as { config: string }).config)).toMatchObject({ accent: 'rose' });
});
