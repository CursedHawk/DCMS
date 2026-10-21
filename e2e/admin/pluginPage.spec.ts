import { test, expect } from '../fixtures/test';

/**
 * A plugin instance as an app (docs/adr/0018, #337): reached from the list of what is installed,
 * and able to show and edit any data set the plugin describes without the console knowing the
 * plugin.
 */

test('opens from the list of installed plugins, on its work', async ({ page }) => {
  await page.goto('/plugins');
  await page.getByTestId('plugin-instance-link').filter({ hasText: 'Press room' }).click();

  // A blog's work is its posts: the collection Content shows, inside the app.
  await expect(page).toHaveURL(/\/apps\/press-room\/content\/post$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Press room' })).toBeVisible();
  await expect(page.getByRole('table').getByText('Hello, world')).toBeVisible();
  // Under Admin, its settings and the platform's pages of it.
  const app = page.getByRole('navigation', { name: 'Press room' });
  for (const name of ['Settings', 'Data', 'Integration', 'Activity']) {
    await expect(app.getByRole('link', { name })).toBeVisible();
  }
});

test('browses, searches and edits a data set the plugin describes', async ({ page, api }) => {
  await page.goto('/apps/press-room/data');

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
  await page.goto('/apps/press-room/data');

  await page.getByRole('table').getByRole('checkbox', { name: 'Select all' }).check();
  await page.getByRole('button', { name: 'Confirm (2)' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();

  await expect.poll(() => api.requestsTo('POST', '/api/admin/plugins/press-room/_data/subscribers/actions/confirm').length).toBe(1);
  expect(api.requestsTo('POST', '/api/admin/plugins/press-room/_data/subscribers/actions/confirm')[0].body).toEqual({ keys: ['s1', 's2'] });
});

test('a view filters a data set on the columns the plugin declares filterable, and hides columns', async ({ page, api }) => {
  await page.goto('/apps/press-room/data');
  const table = page.getByRole('table');
  await expect(table.getByText('ada@example.test')).toBeVisible();

  await page.getByRole('button', { name: 'Filters' }).click();
  await page.getByRole('button', { name: 'Add condition' }).click();
  // Email is the only column the plugin filters on.
  await page.getByRole('combobox', { name: 'Field' }).click();
  await expect(page.getByRole('option')).toHaveText(['Email']);
  await page.keyboard.press('Escape');
  await page.getByRole('textbox', { name: 'Value' }).fill('ada');
  await expect.poll(() => api.requestsTo('GET', '/api/admin/plugins/press-room/_data/subscribers').some((r) =>
    r.query.get('where') === JSON.stringify({ and: [{ field: 'email', op: 'contains', value: 'ada' }] }))).toBe(true);

  await page.getByRole('button', { name: 'Columns' }).click();
  await page.getByRole('checkbox', { name: 'Show Name' }).click();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('columnheader', { name: /Name/ })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /View: .*/ })).toContainText('modified');
});
