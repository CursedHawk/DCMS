import { test, expect } from '../fixtures/test';

/**
 * The Settings area, which is one sidebar entry over seven pages.
 *
 * <p>Two things here are the reason the consolidation is worth testing from outside rather than
 * trusting the unit tests on the section list: the landing page forwards to a section the caller
 * can actually open, and every URL that used to be top-level still arrives somewhere.</p>
 */

test('one Settings entry stands where seven used to', async ({ page }) => {
  await page.goto('/settings');
  const sub = page.getByRole('navigation', { name: 'Settings' });

  await expect(sub.getByRole('link', { name: /Members/ })).toBeVisible();
  await expect(sub.getByRole('link', { name: /Roles/ })).toBeVisible();
  await expect(sub.getByRole('link', { name: /Audit log/ })).toBeVisible();
});

test('the landing page forwards to the first section, and marks it', async ({ page }) => {
  await page.goto('/settings');

  await expect(page).toHaveURL(/\/settings\/general$/);
  await expect(
    page.getByRole('navigation', { name: 'Settings' }).getByRole('link', { name: /General/ }),
  ).toHaveAttribute('aria-current', 'page');
});

test.describe('somebody who holds only audit:read', () => {
  test.use({ grants: ['audit:read'] });

  /*
   * The reason the landing page is a component rather than a router-level redirect. `beforeLoad`
   * runs before permissions have been fetched, so a static redirect would send this person to
   * General and refuse them there — a Settings menu whose front door is a locked door.
   */
  test('lands on the section they can open, not on the first one in the list', async ({ page }) => {
    await page.goto('/settings');
    await expect(page).toHaveURL(/\/settings\/audit$/);
  });

  test('sees only the sections they hold, plus the one open to everyone', async ({ page }) => {
    await page.goto('/settings');
    const sub = page.getByRole('navigation', { name: 'Settings' });

    await expect(sub.getByRole('link', { name: /Audit log/ })).toBeVisible();
    // The API reference names no permission, which is also why /settings is never empty.
    await expect(sub.getByRole('link', { name: /API Docs/ })).toBeVisible();
    // Absent rather than disabled: a column of greyed-out section links teaches nothing and
    // makes the ones you can use harder to find. (Buttons follow the opposite rule.)
    await expect(sub.getByRole('link', { name: /Members/ })).toHaveCount(0);
  });
});

/*
 * Load-bearing. Notification rows carry `linkPath` values like "/members", live in the database
 * for the whole retention window and are read by the bell; the platform console deep-links in
 * here; people bookmark. Each has to arrive somewhere rather than at Not Found.
 */
const MOVED: [string, RegExp][] = [
  ['/members', /\/settings\/members$/],
  ['/roles', /\/settings\/roles$/],
  ['/domains', /\/settings\/domains$/],
  ['/audit', /\/settings\/audit$/],
  ['/ai', /\/settings\/ai$/],
  ['/workspace', /\/settings\/general$/],
  ['/openapi', /\/settings\/api$/],
];

for (const [from, to] of MOVED) {
  test(`${from} still arrives, now at its section`, async ({ page }) => {
    await page.goto(from);
    await expect(page).toHaveURL(to);
  });
}

test('a moved URL redirects rather than refusing, even for somebody who may not open it', async ({
  page,
}) => {
  // The redirect routes are deliberately unguarded: a guard would refuse before the redirect
  // ran, so an old bookmark would show the refusal page instead of the page it was saved for.
  // What the reader gets is the section's own refusal, at the section's own URL.
  await page.goto('/members');
  await expect(page).toHaveURL(/\/settings\/members$/);
});
