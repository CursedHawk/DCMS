import { test, expect } from '../fixtures/test';

/**
 * Plugins' own screens (docs/adr/0019) inside their apps (#337): the console draws the app the
 * server sent, loads the plugin's UI module and renders its screen in the app's frame; old URLs
 * still land there; a switched-off app says so instead of rendering its work.
 */

test("opens an app's screen from the menu", async ({ page }) => {
  await page.goto('/');
  const nav = page.getByRole('navigation', { name: 'Sections' });
  await nav.getByRole('link', { name: 'Contact' }).click();

  // The app opens on its work. The menu marks the app; its pages are the app's own sub-navigation.
  await expect(page).toHaveURL(/\/apps\/contact\/inbox$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Contact' })).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Contact' })).toHaveAttribute('aria-current', 'page');
  await expect(nav.getByRole('link', { name: 'Inbox' })).toHaveCount(0);
  await expect(page.getByRole('navigation', { name: 'Contact' }).getByRole('link', { name: 'Inbox' }))
    .toHaveAttribute('aria-current', 'page');
  // Rendered by the Forms plugin's module, from the plugin's own admin routes.
  await expect(page.getByRole('table').getByText('Do you ship to Brno?')).toBeVisible();
});

test('the old addresses of a screen still work', async ({ page }) => {
  await page.goto('/forms');
  await expect(page).toHaveURL(/\/apps\/contact\/inbox/);
  await expect(page.getByRole('table').getByText('Do you ship to Brno?')).toBeVisible();

  await page.goto('/plugins/press-room?tab=settings');
  await expect(page).toHaveURL(/\/apps\/press-room\/settings$/);
  await expect(page.getByLabel('Name')).toHaveValue('Press room');
});

test('a switched-off app explains itself instead of rendering its work', async ({ page }) => {
  await page.goto('/app/live-chat/console');
  await expect(page).toHaveURL(/\/apps\/chatbot\/console$/);
  await expect(page.getByText('This app is switched off')).toBeVisible();
  await expect(page.getByRole('switch', { name: 'Plugin enabled' })).not.toBeChecked();
});
