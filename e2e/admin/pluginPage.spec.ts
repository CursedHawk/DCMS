import { test, expect } from '../fixtures/test';

/**
 * A plugin instance's own page (docs/adr/0018): reached from the plugin list, and able to show
 * and edit any data set the plugin describes without the console knowing the plugin.
 */

test('opens from the plugin list', async ({ page }) => {
  await page.goto('/plugins');
  await page.getByTestId('plugin-instance-link').filter({ hasText: 'Press room' }).click();

  await expect(page).toHaveURL(/\/plugins\/press-room/);
  await expect(page.getByRole('heading', { level: 1, name: 'Press room' })).toBeVisible();
  // The overview lists the data sets the plugin keeps, with a way into each.
  await expect(page.getByText('Subscribers')).toBeVisible();
});

test('browses, searches and edits a data set the plugin describes', async ({ page, api }) => {
  await page.goto('/plugins/press-room?tab=data');

  const table = page.getByRole('table');
  await expect(table.getByText('ada@example.test')).toBeVisible();
  await expect(table.getByText('max@example.test')).toBeVisible();

  await page.getByRole('searchbox').fill('ada');
  await expect(table.getByText('max@example.test')).toBeHidden();
  expect(api.requestsTo('GET', '/api/admin/plugins/press-room/_data/subscribers').at(-1)?.query.get('search')).toBe('ada');

  await table.getByText('ada@example.test').click();
  const sheet = page.getByRole('dialog');
  await sheet.getByLabel('Name').fill('Ada Lovelace');
  await sheet.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', '/api/admin/plugins/press-room/_data/subscribers/row').length).toBe(1);
  const put = api.requestsTo('PUT', '/api/admin/plugins/press-room/_data/subscribers/row')[0];
  expect(put.query.get('key')).toBe('s1');
  // Only the item schema's values are sent; the email is not the editor's to change.
  expect(put.body).toEqual({ name: 'Ada Lovelace' });
});

test('runs an action on the selected rows', async ({ page, api }) => {
  await page.goto('/plugins/press-room?tab=data');

  await page.getByRole('table').getByRole('checkbox', { name: 'Select all' }).check();
  await page.getByRole('button', { name: 'Confirm (2)' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();

  await expect.poll(() => api.requestsTo('POST', '/api/admin/plugins/press-room/_data/subscribers/actions/confirm').length).toBe(1);
  expect(api.requestsTo('POST', '/api/admin/plugins/press-room/_data/subscribers/actions/confirm')[0].body).toEqual({ keys: ['s1', 's2'] });
});
