import { test, expect } from '../fixtures/test';

/**
 * PageSpeed Insights in the site editors (#649): a finished audit reads as scores and what to fix,
 * and running one asks for the unpublished branch being edited.
 */

const SITE = '11111111-1111-1111-1111-111111111111';
const AUDIT = 'aaaaaaaa-0000-0000-0000-000000000001';

const report = (strategy: 'mobile' | 'desktop') => ({
  strategy,
  finalUrl: 'https://acme.example/',
  scores: { performance: strategy === 'mobile' ? 42 : 91, accessibility: 88, 'best-practices': 100, seo: 90 },
  metrics: [{ id: 'largest-contentful-paint', title: 'Largest Contentful Paint', displayValue: '4.8 s', score: 0.4 }],
  fieldCategory: null,
  field: [],
  issues: [
    {
      id: 'image-alt',
      category: 'accessibility',
      title: 'Image elements do not have [alt] attributes',
      description: 'Informative elements need alternate text. [Learn more](https://developer.chrome.com/docs/lighthouse/accessibility/image-alt/).',
      score: 0,
      displayValue: null,
      savingsMs: null,
      savingsBytes: null,
      items: [{ selector: 'main > img', snippet: '<img src="hero.jpg">' }],
      itemCount: 1,
    },
  ],
  warnings: [],
});

const DONE = {
  id: AUDIT,
  target: 'live',
  branch: null,
  url: 'https://acme.example/',
  status: 'Succeeded',
  error: null,
  jobId: null,
  createdAt: '2026-11-16T20:00:00Z',
  completedAt: '2026-11-16T20:01:00Z',
  scores: { mobile: report('mobile').scores, desktop: report('desktop').scores },
};

test('the IDE shows an audit and runs one of the unpublished branch', async ({ api, page }) => {
  api
    .on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Acme site', renderMode: 'ReactApp', environmentKey: SITE })
    .on('GET', '/api/admin/sites/:id/audits', { configured: true, audits: [DONE] })
    .on('GET', '/api/admin/sites/:id/audits/:auditId', { ...DONE, mobile: report('mobile'), desktop: report('desktop') })
    .on('POST', '/api/admin/sites/:id/audits', { auditId: 'aaaaaaaa-0000-0000-0000-000000000002', jobId: 'job-1' })
    .on('GET', '/api/admin/sites/:id/ide', {
      branch: 'main',
      baseSha: 'abc1234',
      version: 3,
      files: { 'src/App.tsx': 'export default function App() { return null; }\n' },
      hashes: { 'src/App.tsx': 'h' },
    })
    .on('GET', '/api/admin/sites/:id/git/changes', [])
    .on('GET', '/api/admin/sites/:id/git/branches', [{ name: 'main', isDefault: true }])
    .on('GET', '/api/admin/sites/:id/git/history', [])
    .on('GET', '/api/admin/sites/:id/builds', []);
  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);
  await page.locator('button[title="Audit"]').first().click();

  const result = page.getByRole('region', { name: 'Audit result' });
  await expect(result.getByRole('progressbar', { name: 'Performance: 42' })).toBeVisible({ timeout: 25000 });
  await result.getByRole('button', { name: 'Desktop' }).click();
  await expect(result.getByRole('progressbar', { name: 'Performance: 91' })).toBeVisible();

  await result.getByText('Image elements do not have [alt] attributes').click();
  await expect(result.getByRole('link', { name: 'Learn more' })).toHaveAttribute('href', 'https://developer.chrome.com/docs/lighthouse/accessibility/image-alt/');
  await expect(result.getByText('<img src="hero.jpg">')).toBeVisible();
  await expect(result.getByRole('link', { name: /full report in PageSpeed Insights/ })).toHaveAttribute(
    'href', 'https://pagespeed.web.dev/analysis?url=https%3A%2F%2Facme.example%2F');

  await page.getByRole('button', { name: 'Run audit' }).click();
  await expect.poll(() => api.requestsTo('POST', `/api/admin/sites/${SITE}/audits`).map((r) => r.body)).toEqual([
    { target: 'draft', branch: 'main' },
  ]);
});

test('without a PageSpeed key the panel says so and cannot run', async ({ api, page }) => {
  api
    .on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Files site', renderMode: 'StaticFiles', environmentKey: SITE })
    .on('GET', '/api/admin/sites/:id/builds', [])
    .on('GET', '/api/admin/sites/:id/search', {
      settings: { indexable: true, exclude: [], include: [] }, automaticPages: false, pages: null, sitemapUrl: null, environmentKey: SITE,
    })
    .on('GET', '/api/admin/sites/:id/audits', { configured: false, audits: [] });
  await page.goto(`/sites/${SITE}`);

  await expect(page.getByText(/PageSpeed Insights isn't set up on this platform yet/)).toBeVisible({ timeout: 25000 });
  await expect(page.getByRole('button', { name: 'Run audit' })).toBeDisabled();
});
