import { test, expect } from '../fixtures/test';

/**
 * Plugins' own screens (docs/adr/0019): the console draws the menu entry the server sent, loads
 * the plugin's UI module and renders its screen; old URLs still land there; a switched-off
 * plugin says so instead of rendering.
 */

test('opens a plugin screen from the menu', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('navigation', { name: 'Sections' }).getByRole('link', { name: 'Forms' }).click();

  await expect(page).toHaveURL(/\/app\/forms\/inbox$/);
  // Rendered by the Forms plugin's module, from the plugin's own admin routes.
  await expect(page.getByRole('table').getByText('Do you ship to Brno?')).toBeVisible();
});

test('the old address of a screen that moved into its plugin still works', async ({ page }) => {
  await page.goto('/forms');
  await expect(page).toHaveURL(/\/app\/forms\/inbox/);
  await expect(page.getByRole('table').getByText('Do you ship to Brno?')).toBeVisible();
});

test('a switched-off plugin explains itself instead of rendering', async ({ page }) => {
  await page.goto('/app/live-chat/console');
  await expect(page.getByText('AI Chatbot is switched off')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Open AI Chatbot' })).toHaveAttribute('href', /\/plugins\/chatbot/);
});
