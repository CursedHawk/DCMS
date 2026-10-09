import { test, expect } from '../fixtures/test';
import * as data from '../fixtures/data';

/**
 * The Google Analytics plugin's "sites" screen (docs/adr/0023) in the real console against a
 * mocked server: a Measurement ID per site, saved into the instance config through the ordinary
 * instance update, with a malformed ID refused before it is sent and an uploaded (Mode C) site
 * told to add the tag itself.
 */

const SHOP = { id: '11111111-0000-0000-0000-000000000001', name: 'Shop', renderMode: 'StaticPrerender' };
const BLOG = { id: '11111111-0000-0000-0000-000000000002', name: 'Blog', renderMode: 'ReactBuilder' };
const DROP = { id: '11111111-0000-0000-0000-000000000003', name: 'Landing zip', renderMode: 'StaticFiles' };

const INSTANCE = {
  id: 'cccccccc-0000-0000-0000-0000000000a7', pluginId: 'google-analytics', slug: 'google', name: 'Google Analytics',
  description: null, enabled: true, aiToolsEnabled: false,
  config: JSON.stringify({ sites: { [SHOP.id]: 'G-SHOP1234' } }),
};

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/plugin-ui', [
      ...data.PLUGIN_UI,
      {
        pluginId: 'google-analytics', name: 'Google Analytics', icon: 'ChartColumn', source: 'builtin',
        module: { kind: 'builtin', key: 'Dcms.Plugins.GoogleAnalytics' },
        screens: [
          {
            id: 'sites', title: 'Sites', titles: {}, description: null, scope: 'instance', icon: 'Globe',
            permission: 'plugins:manage', nav: null, allowed: true,
          },
        ],
        instances: [{ id: INSTANCE.id, slug: INSTANCE.slug, name: INSTANCE.name, enabled: true }],
      },
    ])
    .on('GET', '/api/admin/plugins/instances', [...data.PLUGIN_INSTANCES, INSTANCE])
    .on('GET', '/api/admin/plugins/catalog', [
      ...data.PLUGIN_CATALOG,
      {
        id: 'google-analytics', name: 'Google Analytics', version: '1.0.0', description: 'GA4 per site.',
        allowMultipleInstances: false, configJsonSchema: '{}', permissions: [], dependencies: [], publicConfigKeys: [],
        contentTypes: [], provides: [], consumes: [],
      },
    ])
    .on('GET', '/api/admin/sites', [SHOP, BLOG, DROP])
    .on('PUT', `/api/admin/plugins/instances/${INSTANCE.id}`, {});
});

test('saves a Measurement ID per site and refuses a malformed one', async ({ page, api }) => {
  await page.goto('/plugins/google/sites');

  const shop = page.getByRole('textbox', { name: 'Measurement ID: Shop' });
  const blog = page.getByRole('textbox', { name: 'Measurement ID: Blog' });
  await expect(shop).toHaveValue('G-SHOP1234');
  await expect(page.getByText('add the Google tag to your bundle yourself')).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Measurement ID: Landing zip' })).toHaveCount(0);

  await blog.fill('ua-123');
  await expect(page.getByText('A Measurement ID looks like')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled();

  await blog.fill('g-blog5678');
  await shop.fill('');
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', `/api/admin/plugins/instances/${INSTANCE.id}`).length).toBe(1);
  const body = api.requestsTo('PUT', `/api/admin/plugins/instances/${INSTANCE.id}`)[0]!.body as { config: string };
  expect(JSON.parse(body.config)).toEqual({ sites: { [BLOG.id]: 'G-BLOG5678' } });
});
