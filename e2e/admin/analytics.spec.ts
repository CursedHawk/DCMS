import { test, expect } from '../fixtures/test';
import * as data from '../fixtures/data';

/**
 * The analytics dashboard refreshes on a period the reader picks (default five minutes, never
 * below one) and otherwise stays quiet: it once refetched in a loop, because the previous
 * period's query key held "now" and so changed on every render.
 */

const EMPTY = {
  range: { from: '2026-09-01T00:00:00Z', to: '2026-10-01T00:00:00Z' },
  summary: { events: 12, pageviews: 10, visitors: 4, sessions: 5 },
  series: [], topPaths: [], byType: [], topSources: [], byCountry: [], byDevice: [], byBrowser: [], byHostname: [], byCampaign: [],
};

test.beforeEach(async ({ api, page }) => {
  await page.clock.install();
  api
    .on('GET', '/api/admin/plugin-ui', [
      ...data.PLUGIN_UI,
      {
        pluginId: 'analytics', name: 'Analytics', icon: 'TrendingUp', source: 'builtin',
        module: { kind: 'builtin', key: 'Dcms.Plugins.Analytics' },
        screens: [
          {
            id: 'dashboard', title: 'Analytics', titles: null, description: null, scope: 'plugin',
            icon: 'TrendingUp', permission: 'analytics:read', nav: { group: 'main', order: 30 }, allowed: true,
          },
        ],
        instances: [{ id: 'cccccccc-0000-0000-0000-0000000000f3', slug: 'analytics', name: 'Analytics', enabled: true }],
      },
    ])
    .on('GET', '/api/admin/analytics', EMPTY)
    .on('GET', '/api/admin/analytics/dimensions', {
      sites: [{ id: '11111111-0000-0000-0000-000000000001', name: 'Shop' }],
      hostnames: [], types: [], countries: [], devices: [],
    });
});

test('loads once, then refreshes on the chosen period', async ({ page, api }) => {
  const fetches = () => api.requestsTo('GET', '/api/admin/analytics').length;

  await page.goto('/app/analytics/dashboard');
  await expect(page.getByRole('combobox', { name: 'Refresh rate' })).toHaveText('Refresh every 5 min');
  // The current and the previous period, once each — and then nothing.
  await expect.poll(fetches).toBe(2);
  await page.clock.runFor(30_000);
  expect(fetches()).toBe(2);

  await page.clock.runFor(5 * 60_000);
  await expect.poll(fetches).toBe(4);

  await page.getByRole('combobox', { name: 'Refresh rate' }).click();
  await page.getByRole('option', { name: 'Refresh every 1 min' }).click();
  await page.clock.runFor(60_000);
  await expect.poll(fetches).toBe(6);
});

test('filters by site, and carries the site into the comparison period', async ({ page }) => {
  await page.goto('/app/analytics/dashboard');
  await page.getByRole('combobox').filter({ hasText: 'Any site' }).click();

  const current = page.waitForRequest((r) => /\/api\/admin\/analytics\?.*days=30.*site=1111/.test(r.url()));
  const previous = page.waitForRequest((r) => /\/api\/admin\/analytics\?.*site=1111.*from=/.test(r.url()));
  await page.getByRole('option', { name: 'Shop' }).click();
  await current;
  await previous;
});
