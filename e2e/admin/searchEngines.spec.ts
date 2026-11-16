import { test, expect } from '../fixtures/test';
import { API_MANIFEST, RUNTIME_MANIFEST, SITE as VISUAL_SITE, builderServer } from '../fixtures/visualSite';

/**
 * A site's say over search engines (#648): the panel every site editor keeps in its rail. Only a
 * Mode D site lists its own pages (each with a switch); every other site's sitemap is exactly the
 * URLs entered. What matters is the one request it sends.
 */

const SITE = '11111111-1111-1111-1111-111111111111';

const MANUAL = {
  settings: { indexable: true, exclude: ['/drafts/*'], include: [] },
  automaticPages: false,
  pages: null,
  sitemapUrl: null,
  environmentKey: SITE,
};

const ideFiles = {
  branch: 'main',
  baseSha: 'abc1234',
  version: 3,
  files: { 'src/App.tsx': 'export default function App() { return null; }\n' },
  hashes: { 'src/App.tsx': 'h' },
};

test('in the IDE, a React site lists the URLs entered, hides a path and can hide the whole site', async ({ api, page }) => {
  api
    .on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Acme site', renderMode: 'ReactApp', environmentKey: SITE })
    .on('GET', '/api/admin/sites/:id/search', MANUAL)
    .on('PUT', '/api/admin/sites/:id/search', ({ body }) => body)
    .on('GET', '/api/admin/sites/:id/ide', ideFiles)
    .on('GET', '/api/admin/sites/:id/git/changes', [])
    .on('GET', '/api/admin/sites/:id/git/branches', [{ name: 'main', isDefault: true }])
    .on('GET', '/api/admin/sites/:id/git/history', [])
    .on('GET', '/api/admin/sites/:id/builds', []);
  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);
  await page.locator('button[title="Search engines"]').first().click();

  const urls = page.getByRole('textbox', { name: 'Sitemap URLs' });
  await expect(urls).toBeVisible({ timeout: 25000 });
  // Nothing is found for it: no list of pages to switch.
  await expect(page.getByRole('region', { name: 'Pages in the sitemap' })).toHaveCount(0);

  for (const path of ['/', '/about']) {
    await urls.fill(path);
    await urls.press('Enter');
  }
  await page.getByRole('textbox', { name: 'Hidden from search engines' }).fill('/private/*');
  await page.getByRole('textbox', { name: 'Hidden from search engines' }).press('Enter');
  await page.getByRole('switch', { name: /Allow search engines/ }).click();
  await expect(page.getByText(/Hidden from search engines: every page says noindex/)).toBeVisible();
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', `/api/admin/sites/${SITE}/search`).map((r) => r.body)).toEqual([
    { indexable: false, exclude: ['/drafts/*', '/private/*'], include: ['/', '/about'] },
  ]);
});

test('a Mode C site has the same panel on its page', async ({ api, page }) => {
  api
    .on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Files site', renderMode: 'StaticFiles', environmentKey: SITE })
    .on('GET', '/api/admin/sites/:id/search', MANUAL)
    .on('PUT', '/api/admin/sites/:id/search', ({ body }) => body)
    .on('GET', '/api/admin/sites/:id/builds', []);
  await page.goto(`/sites/${SITE}`);

  const hidden = page.getByRole('textbox', { name: 'Hidden from search engines' });
  await expect(hidden).toBeVisible({ timeout: 25000 });
  await hidden.fill('about');
  await expect(page.getByText('Start the path with /.')).toBeVisible();
  await page.getByRole('textbox', { name: 'Sitemap URLs' }).fill('/');
  await page.getByRole('textbox', { name: 'Sitemap URLs' }).press('Enter');
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', `/api/admin/sites/${SITE}/search`).map((r) => r.body)).toEqual([
    { indexable: true, exclude: ['/drafts/*'], include: ['/'] },
  ]);
});

test('a Mode D site lists its own pages, each with a switch', async ({ api, page }) => {
  api
    .on('GET', '/api/admin/sites/:id/search', {
      settings: { indexable: true, exclude: ['/drafts/*'], include: [] },
      automaticPages: true,
      pages: [
        { path: '/' },
        { path: '/about' },
        { path: '/drafts/one' },
        { path: '/blog/:slug', instance: 'blog', contentType: 'post' },
      ],
      sitemapUrl: 'https://acme.example/sitemap.xml',
      environmentKey: VISUAL_SITE,
    })
    .on('PUT', '/api/admin/sites/:id/search', ({ body }) => body);
  // The visual builder's rail, no canvas needed: the generated layers' stand-ins, as `open` passes them.
  builderServer(api, {
    'src/api/manifest.json': API_MANIFEST,
    'src/dcms/runtime/index.ts': '// runtime\n',
    'src/dcms/runtime/manifest.json': RUNTIME_MANIFEST,
  });
  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${VISUAL_SITE}`);
  await page.locator('button[title="Search engines"]').first().click();

  const pages = page.getByRole('region', { name: 'Pages in the sitemap' });
  await expect(pages.getByText('Each published item · blog/post')).toBeVisible({ timeout: 25000 });
  // A page under a section rule is hidden by it, and only the rule can show it again.
  await expect(pages.getByRole('switch', { name: '/drafts/one' })).toBeDisabled();
  await expect(pages.getByText('Hidden by /drafts/*')).toBeVisible();
  await pages.getByRole('switch', { name: '/blog/:slug' }).click();
  await expect(page.getByRole('textbox', { name: 'Extra URLs' })).toBeVisible();
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', `/api/admin/sites/${VISUAL_SITE}/search`).map((r) => r.body)).toEqual([
    { indexable: true, exclude: ['/drafts/*', '/blog/:slug'], include: [] },
  ]);
});
