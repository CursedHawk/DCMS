import { test, expect } from '../fixtures/test';
import * as data from '../fixtures/data';

/**
 * The YouTube plugin's settings: a pasted channel link is looked up once typing pauses and shown
 * as the channel it resolves to, a link to nothing says so, and Sync now runs the sync with the
 * saved settings through the plugin's own route.
 */

test.use({ grants: [...data.ALL_TENANT_PERMISSIONS, 'plugin:youtube:sync'] });

const INSTANCE = {
  id: 'cccccccc-0000-0000-0000-0000000000b1', pluginId: 'youtube', slug: 'videos', name: 'Videos',
  description: null, enabled: true, aiToolsEnabled: false, config: JSON.stringify({ channel: '@acme' }),
};

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/plugin-ui', [
      ...data.PLUGIN_UI,
      {
        pluginId: 'youtube', name: 'YouTube', icon: 'Youtube', source: 'builtin',
        module: { kind: 'builtin', key: 'Dcms.Plugins.YouTube' },
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
        id: 'youtube', name: 'YouTube', version: '1.0.0', description: 'Latest videos.', allowMultipleInstances: true,
        configJsonSchema: JSON.stringify({
          type: 'object',
          properties: {
            channel: { type: 'string', format: 'youtube-channel', title: 'Channel' },
            maxVideos: { type: 'integer', title: 'Videos to keep', default: 12 },
          },
        }),
        permissions: [], dependencies: [], publicConfigKeys: [], contentTypes: [], provides: [], consumes: [],
      },
    ])
    .on('GET', '/api/admin/plugins/videos/resolve', ({ query }) =>
      query.get('channel') === '@acme'
        ? { channel: { id: 'UCacme', title: 'Acme Bakery', handle: '@acme', avatarUrl: null }, playlist: null, error: null }
        : { channel: null, playlist: null, error: `No YouTube channel matches “${query.get('channel')}”.` })
    .on('POST', '/api/admin/plugins/videos/sync', { created: 4, updated: 8, removed: 1 });
});

test('shows the channel a link resolves to and syncs it', async ({ page, api }) => {
  await page.goto('/apps/videos/settings');

  await expect(page.getByText('Acme Bakery')).toBeVisible();
  await page.getByRole('button', { name: 'Sync now' }).click();

  await expect(page.getByText('Synced: 4 new, 8 refreshed, 1 removed.')).toBeVisible();
  expect(api.requestsTo('POST', '/api/admin/plugins/videos/sync')).toHaveLength(1);

  await page.getByRole('textbox', { name: 'Channel' }).fill('@nobody');
  await expect(page.getByText('No YouTube channel matches “@nobody”.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sync now' })).toHaveCount(0);
});
