import { test, expect } from '../fixtures/test';

/**
 * A Functions app's IDE: packages have their own sidebar view (list, version change, update to
 * latest, remove, search and add), and an editor tab has no dead pixels around its pin.
 */

const REPO = '55555555-5555-5555-5555-555555555555';
const FILES = {
  'functions/hello.ts': 'export default {};\n',
  'package.json': JSON.stringify({ name: 'functions', private: true, dependencies: { zod: '^3.22.0' } }, null, 2),
};
const NEXT = JSON.stringify({ name: 'functions', private: true, dependencies: { zod: '~3.23.0' } }, null, 2);

test('packages view manages packages; a tab is clickable edge to edge', async ({ page, api }) => {
  test.setTimeout(120_000);
  api
    .on('GET', '/api/admin/repos/:id/ide', {
      branch: 'main',
      baseSha: 'abc1234',
      version: 1,
      files: FILES,
      hashes: Object.fromEntries(Object.keys(FILES).map((p) => [p, `h-${p}`])),
    })
    .on('PATCH', '/api/admin/repos/:id/ide/files', { version: 2, hashes: {} })
    .on('GET', '/api/admin/repos/:id/git/changes', [])
    .on('GET', '/api/admin/repos/:id/git/branches', [{ name: 'main', isDefault: true }])
    .on('GET', '/api/admin/repos/:id/git/history', [])
    .on('GET', '/api/admin/plugins/:slug/_functions/sdk', { typings: 'export {};', csharp: '', etag: '"e"' })
    .on('POST', '/api/admin/plugins/:slug/_functions/check', { ok: true, problems: [], functions: [], permissions: [], specs: [] })
    .on('GET', '/api/admin/plugins/:slug/_functions/deployments', [])
    .on('POST', '/api/admin/plugins/:slug/_functions/packages/types', {})
    .on('GET', '/api/admin/plugins/:slug/_functions/packages/search', [{ name: 'lodash', version: '4.17.21', description: 'Utilities' }])
    .on('POST', '/api/admin/plugins/:slug/_functions/packages', { files: { 'package.json': NEXT } })
    .on('GET', '/api/admin/ai/conversations', []);

  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/repos/${REPO}/code?name=Automations&slug=automations&kind=functions`);
  await page.getByRole('button', { name: 'Packages' }).click({ timeout: 60_000 });
  const view = page.getByTestId('functions-packages');
  await expect(view.getByTestId('package-zod')).toBeVisible();

  // A new range, applied with Enter: the server writes the manifest, the row follows it.
  await view.getByLabel('Version of zod').fill('~3.23.0');
  await view.getByLabel('Version of zod').press('Enter');
  await expect(view.getByLabel('Version of zod')).toHaveValue('~3.23.0');
  const posts = () => api.requests.filter((r) => r.method === 'POST' && r.path.endsWith('/_functions/packages'));
  expect(posts()[0]?.body).toMatchObject({ ecosystem: 'npm', name: 'zod', version: '~3.23.0', remove: false });

  // Update to latest sends no version; remove sends remove.
  await view.getByRole('button', { name: 'Update zod to the latest version' }).click();
  await expect.poll(() => posts().length).toBe(2);
  expect(posts()[1]?.body).toMatchObject({ name: 'zod', remove: false });
  expect(posts()[1]?.body).not.toHaveProperty('version');
  await view.getByRole('button', { name: 'Remove zod' }).click();
  await expect.poll(() => posts().length).toBe(3);
  expect(posts()[2]?.body).toMatchObject({ name: 'zod', remove: true });

  // Search and add.
  await view.getByLabel('Search packages…').fill('lod');
  await view.getByTestId('functions-packages-found').getByRole('button', { name: 'Add' }).click();
  await expect.poll(() => posts().length).toBe(4);
  expect(posts()[3]?.body).toMatchObject({ name: 'lodash', remove: false });

  // The manifest opens as a tab; every point across that tab is one of its buttons.
  await view.getByRole('button', { name: 'Open package.json' }).click();
  const tab = page.locator('button[title="package.json"]').locator('..');
  await expect(tab).toBeVisible();
  const dead = await tab.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const misses: number[] = [];
    for (let x = Math.ceil(r.left) + 1; x < r.right - 1; x++) {
      const hit = document.elementFromPoint(x, r.top + r.height / 2);
      if (!hit?.closest('button') || !el.contains(hit)) misses.push(Math.round(x - r.left));
    }
    return misses;
  });
  expect(dead).toEqual([]);
});
