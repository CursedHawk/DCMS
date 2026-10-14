import { test, expect } from '../fixtures/test';

/**
 * #277: the Deploy panel says where each branch is live, and adds a branch that publishes to
 * its own domains — with a one-click subdomain for it.
 */

const SITE = '44444444-4444-4444-4444-444444444444';
const FILES = { 'src/App.tsx': 'export default function App() {\n  return <main>Hello</main>;\n}\n' };

test('a domain is pointed at a publishing branch, and then cannot be the primary', async ({ page, api }) => {
  let branch: string | null = null;
  api
    .on('GET', '/api/admin/sites', [{ id: SITE, name: 'Acme site', renderMode: 'ReactApp' }])
    .on('GET', '/api/admin/domains', () => [
      {
        id: 'd1', hostname: 'dev.acme.example', verified: true, isPrimary: false, managed: false, siteId: SITE,
        branch, siteName: 'Acme site', txtRecord: '', txtValue: '',
      },
    ])
    .on('GET', '/api/admin/sites/:id/publish-branches', [
      { branch: 'release', release: true, activeBuildId: null, domains: [] },
      { branch: 'dev', release: false, activeBuildId: null, domains: [] },
    ])
    .on('POST', '/api/admin/domains/:id/site', ({ body }) => {
      branch = (body as { branch: string | null }).branch;
      return {};
    });

  await page.goto('/settings/domains');
  await expect(page.getByRole('button', { name: /Make primary/i })).toBeVisible();
  await page.getByRole('combobox', { name: 'Branch' }).click();
  await page.getByRole('option', { name: 'dev' }).click();

  await expect.poll(() => api.requestsTo('POST', '/api/admin/domains/d1/site').map((r) => r.body))
    .toEqual([{ siteId: SITE, branch: 'dev' }]);
  await expect(page.getByRole('button', { name: /Make primary/i })).toHaveCount(0);
});

test('a branch publishes to its own domain from the Deploy panel', async ({ page, api }) => {
  let targets = [
    {
      branch: 'release', release: true, activeBuildId: 'b1',
      domains: [{ id: 'd1', hostname: 'www.acme.example', verified: true, primary: true }],
    },
  ];
  api
    .on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Acme site', renderMode: 'ReactApp', slug: 'acme-site' })
    .on('GET', '/api/admin/sites/:id/ide', {
      branch: 'main', baseSha: 'abc1234', version: 1, files: FILES,
      hashes: Object.fromEntries(Object.keys(FILES).map((p) => [p, `h-${p}`])),
    })
    .on('GET', '/api/admin/sites/:id/git/changes', [])
    .on('GET', '/api/admin/sites/:id/git/branches', [{ name: 'main' }, { name: 'release' }, { name: 'dev' }])
    .on('GET', '/api/admin/sites/:id/git/history', [])
    .on('GET', '/api/admin/sites/:id/builds', [
      {
        id: 'b2', branch: 'dev', status: 'Building', shortSha: 'ddd1111', hasLog: false,
        createdAt: '2026-10-14T10:00:00Z', active: false,
      },
    ])
    .on('GET', '/api/admin/sites/:id/publish-branches', () => targets)
    .on('POST', '/api/admin/sites/:id/publish-branches', ({ body }) => {
      targets = [...targets, { branch: (body as { branch: string }).branch, release: false, activeBuildId: null, domains: [] }];
      return { branch: 'dev', buildId: 'b3' };
    })
    .on('POST', '/api/admin/domains/provisioned', { id: 'd2', hostname: '0f0f.dcms.highgeek.eu', verified: true, isPrimary: false })
    .on('POST', '/api/admin/domains/:id/site', () => {
      targets = targets.map((t) =>
        t.branch === 'dev'
          ? { ...t, domains: [{ id: 'd2', hostname: '0f0f.dcms.highgeek.eu', verified: true, primary: false }] }
          : t,
      );
      return {};
    })
    .on('GET', '/api/admin/ai/conversations', []);

  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);
  await expect(page.getByRole('tab', { name: /Problems/i })).toBeVisible({ timeout: 25000 });
  await page.getByRole('button', { name: 'Deployments' }).click();

  const panel = page.getByRole('region', { name: 'Where it is live' });
  await expect(panel.getByRole('link', { name: 'www.acme.example' })).toBeVisible();
  // A build of another branch says which.
  await expect(page.getByText('dev', { exact: true })).toBeVisible();

  // Release and the branches already publishing are not offered again.
  await panel.getByRole('combobox', { name: 'Publish another branch…' }).click();
  await expect(page.getByRole('option')).toHaveText(['main', 'dev']);
  await page.getByRole('option', { name: 'dev' }).click();
  await panel.getByRole('button', { name: 'Publish' }).click();
  await expect.poll(() => api.requestsTo('POST', `/api/admin/sites/${SITE}/publish-branches`).map((r) => r.body))
    .toEqual([{ branch: 'dev' }]);

  await expect(panel.getByText('No domain yet')).toBeVisible();
  await panel.getByRole('button', { name: 'Add a subdomain' }).click();
  await expect(panel.getByRole('link', { name: '0f0f.dcms.highgeek.eu' })).toBeVisible();
  expect(api.requestsTo('POST', '/api/admin/domains/d2/site').map((r) => r.body)).toEqual([{ siteId: SITE, branch: 'dev' }]);
});
