import { test, expect } from '../fixtures/test';

/**
 * Settings → Usage: the breakdown, ranked, opening to its items; the storage limit beside it;
 * and Measure now, including the refusal when it was measured moments ago.
 */

test('shows what the workspace stores, largest first, and opens a category', async ({ page }) => {
  await page.goto('/settings/usage');

  await expect(page.getByRole('heading', { name: 'Usage', level: 1 })).toBeVisible();
  await expect(page.getByText('6.0 MB')).toBeVisible();
  await expect(page.getByText('Measured 3 hours ago')).toBeVisible();
  await expect(page.getByRole('meter', { name: 'Storage limit' })).toBeVisible();

  // Largest first.
  const rows = page.locator('button[aria-controls^="usage-"]');
  await expect(rows.first()).toContainText('Dynamic Apps');
  await expect(rows.last()).toContainText('Media');

  await page.getByRole('button', { name: /Sites/ }).click();
  await expect(page.getByText('Marketing (earlier builds)')).toBeVisible();
  await expect(page.getByText('Marketing (git repository)')).toBeVisible();

  await page.getByRole('button', { name: /Dynamic Apps/ }).click();
  await expect(page.getByText('12,400 rows')).toBeVisible();
  await expect(page.getByText('Marketing (earlier builds)')).toHaveCount(0);
});

test('Measure now replaces the snapshot', async ({ page, api }) => {
  let calls = 0;
  api.on('POST', '/api/admin/usage/recalculate', () => {
    calls += 1;
    return { measuredAt: new Date().toISOString(), totalBytes: 1024, quotaBytes: 5 * 1024 ** 3, mediaBytes: 0, categories: [
      { key: 'workspace', bytes: 1024, items: [{ key: 'members', label: 'Members, roles and invitations', kind: 'database', bytes: 1024, rows: 3 }] },
    ] };
  });
  await page.goto('/settings/usage');
  await page.getByRole('button', { name: 'Measure now' }).first().click();

  await expect(page.getByText('Usage measured')).toBeVisible();
  await expect(page.getByRole('button', { name: /Workspace/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Dynamic Apps/ })).toHaveCount(0);
  expect(calls).toBe(1);
});

test('measuring again straight away says so instead of failing', async ({ page, api }) => {
  api.on('POST', '/api/admin/usage/recalculate', async ({ route }: { route: import('@playwright/test').Route }) => {
    await route.fulfill({ status: 429, contentType: 'application/problem+json', body: JSON.stringify({ status: 429 }) });
    return undefined;
  });
  await page.goto('/settings/usage');
  await page.getByRole('button', { name: 'Measure now' }).first().click();
  await expect(page.getByText('Usage was measured moments ago')).toBeVisible();
});

test('before the first measurement it offers to measure', async ({ page, api }) => {
  api.on('GET', '/api/admin/usage', { measuredAt: null, totalBytes: 0, quotaBytes: 5 * 1024 ** 3, mediaBytes: 0, categories: [] });
  await page.goto('/settings/usage');
  await expect(page.getByText('Usage is measured once a day.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Measure now' })).toHaveCount(2);
});
