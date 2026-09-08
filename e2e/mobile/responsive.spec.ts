import { test, expect } from '../fixtures/test';

/**
 * The phone, on a Pixel 5.
 *
 * <p>The admin sidebar was a fixed `<aside>` with no breakpoint and no drawer: on a phone the
 * rail took the screen and the page sat behind it. These are the three shapes the redesign
 * promised instead — a drawer, cards, and an honest refusal where a desktop layout cannot be
 * made to work.</p>
 *
 * <p>Chromium only. Mobile Safari would need webkit, which is not installed here, so this makes
 * no claim about iOS.</p>
 */

test('the sidebar is a drawer, not a rail', async ({ page }) => {
  await page.goto('/');

  // Not on screen until asked for.
  const nav = page.getByRole('navigation', { name: 'Sections' });
  await expect(nav).toBeHidden();

  await page.getByRole('button', { name: 'Open navigation' }).click();
  await expect(nav).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Media' })).toBeVisible();
});

test('choosing a destination closes the drawer behind you', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Open navigation' }).click();

  await page.getByRole('navigation', { name: 'Sections' }).getByRole('link', { name: 'Media' }).click();

  await expect(page).toHaveURL(/\/media$/);
  // A drawer left open over the page you just asked for is worse than no drawer.
  await expect(page.getByRole('navigation', { name: 'Sections' })).toBeHidden();
});

test('a list becomes cards rather than a table nobody can read sideways', async ({ page }) => {
  await page.goto('/content');

  /*
   * `DataTable` renders both layouts and lets CSS choose, so both strings are in the document.
   * The claim is about which one the reader gets: the card is visible and the table is not.
   */
  await expect(page.getByRole('table')).toBeHidden();
  await expect(page.locator('li').getByText('Hello, world')).toBeVisible();
});

test('the page never scrolls sideways', async ({ page }) => {
  await page.goto('/media');
  await expect(page.getByText('logo.png')).toBeVisible();

  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(1);
});

test('the IDE refuses the screen instead of rendering badly on it', async ({ page }) => {
  await page.goto('/sites/11111111-1111-1111-1111-111111111111');

  /*
   * Explicitly out of scope for mobile, and said out loud. A code editor with a file tree, a
   * Monaco pane and a live preview does not become usable at 393px by stacking — the honest
   * answer is a panel that says so, with a way through for somebody who insists.
   */
  await expect(page.getByText(/wider screen/i)).toBeVisible();
});
