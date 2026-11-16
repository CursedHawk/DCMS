import { test, expect } from '../fixtures/test';

/**
 * A site's say over search engines (#648): the panel every site editor keeps in its rail, here
 * in the Mode B IDE and on the Mode C page. What matters is the one request it sends.
 */

const SITE = '11111111-1111-1111-1111-111111111111';

const VIEW = {
  settings: { indexable: true, exclude: ['/drafts/*'], include: [] },
  pages: [
    { path: '/' },
    { path: '/about' },
    { path: '/drafts/one' },
    { path: '/blog/:slug', instance: 'blog', contentType: 'post' },
  ],
  sitemapUrl: 'https://acme.example/sitemap.xml',
  environmentKey: SITE,
};

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/sites/:id/search', VIEW)
    .on('PUT', '/api/admin/sites/:id/search', ({ body }) => body)
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
});

test('the IDE rail hides a page, adds a URL and can hide the whole site', async ({ api, page }) => {
  api.on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Acme site', renderMode: 'ReactApp', environmentKey: SITE });
  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);
  await page.locator('button[title="Search engines"]').first().click();

  const panel = page.getByRole('region', { name: 'Pages in the sitemap' });
  await expect(panel.getByText('Each published item · blog/post')).toBeVisible({ timeout: 25000 });
  // A page under a section rule is hidden by it, and only the rule can show it again.
  await expect(panel.getByRole('switch', { name: '/drafts/one' })).toBeDisabled();
  await expect(panel.getByText('Hidden by /drafts/*')).toBeVisible();

  await panel.getByRole('switch', { name: '/about' }).click();
  await page.getByRole('textbox', { name: 'Extra URLs' }).fill('/landing');
  await page.getByRole('textbox', { name: 'Extra URLs' }).press('Enter');
  await page.getByRole('switch', { name: /Allow search engines/ }).click();
  await expect(page.getByText(/Hidden from search engines: every page says noindex/)).toBeVisible();
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', `/api/admin/sites/${SITE}/search`).map((r) => r.body)).toEqual([
    { indexable: false, exclude: ['/drafts/*', '/about'], include: ['/landing'] },
  ]);
});

test('a Mode C site has the same panel on its page', async ({ api, page }) => {
  api.on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Files site', renderMode: 'StaticFiles', environmentKey: SITE });
  await page.goto(`/sites/${SITE}`);

  await expect(page.getByRole('switch', { name: '/about' })).toBeVisible({ timeout: 25000 });
  await page.getByRole('textbox', { name: 'Hidden from search engines' }).fill('about');
  await expect(page.getByText('Start the path with /.')).toBeVisible();
  await page.getByRole('textbox', { name: 'Hidden from search engines' }).fill('/blog/:slug');
  await page.getByRole('textbox', { name: 'Hidden from search engines' }).press('Enter');
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', `/api/admin/sites/${SITE}/search`).map((r) => r.body)).toEqual([
    { indexable: true, exclude: ['/drafts/*', '/blog/:slug'], include: [] },
  ]);
});
