import { test, expect } from '../fixtures/test';

/**
 * The marketplace, and the one thing it exists to do that a plugin list did not: say what
 * adding something will cost you, before you add it.
 */

/*
 * The grid tile, not the sidebar entry: an installed plugin that contributes a menu entry has
 * its name in both places, and `getByText` alone finds the wrong one.
 */
const card = (page: import('@playwright/test').Page, name: string) =>
  page.getByRole('button').filter({ hasText: name }).first();

test('lists what the platform can do, and what is already added', async ({ page }) => {
  await page.goto('/marketplace');

  await expect(card(page, 'Blog')).toBeVisible();
  await expect(card(page, 'Forms')).toBeVisible();
  // The one already in the workspace is marked as such, so "add" never means "add a second"
  // by accident.
  await expect(page.getByText(/Added/)).toBeVisible();
});

test('states the permissions a plugin will request before it is added', async ({ page }) => {
  await page.goto('/marketplace');
  await card(page, 'Forms').click();

  const sheet = page.getByRole('dialog');
  await expect(sheet.getByText('Permissions it requests')).toBeVisible();
  // Named keys, not a count. "This plugin requests 2 permissions" tells a workspace owner
  // nothing they can act on.
  await expect(sheet.getByText('forms:manage')).toBeVisible();

  /*
   * And the circled "i" beside the heading explains what "requests" means, on demand.
   *
   * A popover, not a tooltip — which is why the sentence is not in the DOM until it is asked
   * for. Asserting on the affordance and then on what it says is the honest version of the
   * claim; asserting on the text alone would pass just as well if the button had no handler.
   */
  await sheet.getByRole('button', { name: 'Permissions it requests' }).click();
  await expect(
    page.getByText(/Nobody holds them until you add them to a role/),
  ).toBeVisible();
});

test('says whether it will appear in the menu', async ({ page }) => {
  await page.goto('/marketplace');
  await card(page, 'Forms').click();

  await expect(page.getByRole('dialog').getByText('Adds an entry to your sidebar')).toBeVisible();
});

test('filters down to nothing honestly', async ({ page }) => {
  await page.goto('/marketplace');

  await page.getByPlaceholder('Search plugins').fill('nothing matches this');

  await expect(page.getByText('Nothing matches that')).toBeVisible();
  await expect(page.getByText('Try a different word')).toBeVisible();
});
