import { test, expect } from '../fixtures/test';
import * as data from '../fixtures/data';

/**
 * The WebSockets plugin's channels (ADR 0037): the settings' channel builder turns clicks into
 * the payload JSON Schema that Save sends, and the IDE's Channels view opens the same document as
 * channels.json and saves it through the plugin's own route.
 */

const INSTANCE = {
  id: 'cccccccc-0000-0000-0000-0000000000c7', pluginId: 'websockets', slug: 'rt', name: 'Realtime',
  description: null, enabled: true, aiToolsEnabled: false,
  config: JSON.stringify({
    channels: [{
      name: 'chat', keyed: true, subscribe: 'public', publish: 'subscribers',
      schema: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] },
    }],
  }),
};
const CHANNELS = JSON.parse(INSTANCE.config) as { channels: unknown[] };
const FILE_SCHEMA = {
  type: 'object',
  properties: { channels: { type: 'array' } },
  $defs: { payload: { type: 'object' } },
};

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/plugin-ui', [
      ...data.PLUGIN_UI,
      {
        pluginId: 'websockets', name: 'WebSockets', icon: 'RadioTower', source: 'builtin',
        module: { kind: 'builtin', key: 'Dcms.Plugins.WebSockets' },
        screens: [
          {
            id: 'settings', title: 'Settings', titles: {}, description: null, scope: 'instance', icon: 'Settings',
            permission: 'plugins:manage', section: 'admin', allowed: true,
          },
        ],
        instances: [{ id: INSTANCE.id, slug: INSTANCE.slug, name: INSTANCE.name, enabled: true }],
      },
    ])
    .on('GET', '/api/admin/plugins/instances', [...data.PLUGIN_INSTANCES, INSTANCE])
    .on('GET', '/api/admin/plugins/catalog', [
      ...data.PLUGIN_CATALOG,
      {
        id: 'websockets', name: 'WebSockets', version: '1.0.0', description: 'Realtime channels.', allowMultipleInstances: true,
        configJsonSchema: JSON.stringify({
          type: 'object',
          properties: { channels: { type: 'array', format: 'websocket-channels', title: 'Channels', items: { type: 'object' } } },
        }),
        permissions: [], dependencies: [], publicConfigKeys: [], contentTypes: [], provides: [], consumes: [],
      },
    ])
    .on('GET', '/api/admin/plugins/rt/channels/schema', FILE_SCHEMA)
    .on('GET', '/api/admin/plugins/rt/channels', CHANNELS)
    .on('PUT', '/api/admin/plugins/rt/channels', CHANNELS)
    .on('PUT', '/api/admin/plugins/instances/:id', ({ route }) => route.fulfill({ status: 204 }).then(() => undefined));
});

test('the channel builder writes the payload schema the settings save', async ({ page, api }) => {
  await page.goto('/apps/rt/settings');

  await expect(page.getByRole('textbox', { name: 'Channel name' })).toHaveValue('chat');
  await page.getByRole('button', { name: 'Add field' }).click();
  await page.getByRole('textbox', { name: 'Field name' }).nth(1).fill('sentAt');

  // The TypeScript the clients will get, from the same schema.
  await page.getByRole('tab', { name: 'TypeScript' }).click();
  await expect(page.getByText(/sentAt\?: string/)).toBeVisible();

  await page.getByRole('button', { name: 'Save' }).click();
  await expect.poll(() => api.requestsTo('PUT', `/api/admin/plugins/instances/${INSTANCE.id}`).length).toBe(1);
  const put = api.requestsTo('PUT', `/api/admin/plugins/instances/${INSTANCE.id}`)[0];
  expect(JSON.parse((put.body as { config: string }).config)).toMatchObject({
    channels: [{ name: 'chat', schema: { properties: { text: { type: 'string' }, sentAt: { type: 'string' } }, required: ['text'] } }],
  });
});

test('the IDE opens the channels as channels.json and saves them', async ({ page, api }) => {
  const SITE = '33333333-3333-3333-3333-333333333333';
  const FILES = { 'src/App.tsx': 'export default function App() {\n  return <main>Hello</main>;\n}\n' };
  api
    .on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Acme site', renderMode: 'ReactApp', slug: 'acme-site' })
    .on('GET', '/api/admin/sites/:id/ide', {
      branch: 'main', baseSha: 'abc1234', version: 1, files: FILES,
      hashes: Object.fromEntries(Object.keys(FILES).map((p) => [p, `h-${p}`])),
    })
    .on('GET', '/api/admin/sites/:id/git/changes', [])
    .on('GET', '/api/admin/sites/:id/git/branches', [{ name: 'main', isDefault: true }])
    .on('GET', '/api/admin/sites/:id/git/history', [])
    .on('GET', '/api/admin/sites/:id/builds', [])
    .on('GET', '/api/admin/ai/conversations', []);

  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);
  await expect(page.getByRole('tab', { name: /Problems/i })).toBeVisible({ timeout: 25000 });

  await page.getByRole('button', { name: 'Channels', exact: true }).click();
  await page.getByRole('button', { name: /Realtime/ }).click();

  const dialog = page.getByRole('dialog', { name: 'Realtime: channels.json' });
  await expect(dialog.locator('.monaco-editor .view-lines')).toContainText('"chat"');
  await dialog.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', '/api/admin/plugins/rt/channels').length).toBe(1);
  expect(api.requestsTo('PUT', '/api/admin/plugins/rt/channels')[0].body).toMatchObject({ channels: [{ name: 'chat' }] });
});
