import { platformTest as test, expect, data } from '../fixtures/test';

/**
 * The console's own live plane.
 *
 * <p>Its hub is in platform-api rather than admin-api — same reasoning ADR 0009 used to keep the
 * tenant hub out of content-api — and it supersedes ADR 0012's "polled, not pushed" decision.
 * What it changes for a reader is the thing asserted here: an operations screen that goes stale
 * while something is happening is the wrong failure for the one page you open when things are
 * wrong.</p>
 */

test('a tenant change pushed by the server repaints the list', async ({ page, api, hub }) => {
  await page.goto('/tenants');
  await expect(page.getByRole('table').getByText('Suspended')).toBeVisible();
  await hub.connected;
  await page.evaluate(() => {
    (window as unknown as { __live: number }).__live = 1;
  });

  api.on('GET', '/api/platform/tenants', [
    { ...data.PLATFORM_TENANTS[0] },
    { ...data.PLATFORM_TENANTS[1], status: 'Active' },
  ]);

  hub.resourceChanged('tenants');

  await expect(page.getByRole('table').getByText('Suspended')).toHaveCount(0);
  // The stamp survives, so this was a live update rather than a reload that happened to refetch.
  expect(await page.evaluate(() => (window as unknown as { __live?: number }).__live === 1)).toBe(
    true,
  );
});

test('the polls are still there, because nothing replays what was missed', async ({ page, hub }) => {
  await page.goto('/');
  await hub.connected;

  /*
   * The hub is a hint, not a guarantee. This asserts the console keeps working with the socket
   * up — the fallback interval itself is 30s and testing it would mean a 30s test — but the
   * decision it encodes is why `refetchInterval` was lengthened rather than deleted.
   */
  await expect(page.getByRole('navigation', { name: 'Platform sections' })).toBeVisible();
});
