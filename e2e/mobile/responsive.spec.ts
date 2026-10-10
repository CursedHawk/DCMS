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

/*
 * Every control on the page is on the screen.
 *
 * The bar across the top held nine controls and a 176px workspace switcher in 336px of room, so
 * on a phone the language, theme and account buttons sat past the right edge where nothing could
 * reach them — on every page. Tab strips, header actions and grid cards did the same thing on
 * the pages listed here. 360px rather than the Pixel 5's 393: it is the narrowest Android width
 * still common, and a layout that fits it fits the rest.
 */
const PAGES = [
  '/', '/content', '/media', '/sites', '/plugins', '/plugins/press-room', '/marketplace',
  '/marketplace/forms', '/notifications', '/settings/members', '/settings/roles',
  '/settings/domains', '/app/forms/inbox',
];

test.describe('at 360px', () => {
  test.use({ viewport: { width: 360, height: 740 } });

  for (const path of PAGES) {
    test(`nothing on ${path} sits past the edge of the screen`, async ({ page }) => {
      await page.goto(path);
      await expect(page.getByRole('button', { name: 'Account settings' })).toBeVisible();
      await page.waitForLoadState('networkidle');

      // Off the screen, or cut off by a box that clips it: both are a control nobody can tap.
      const unreachable = await page.evaluate(() => {
        const out: string[] = [];
        for (const el of document.querySelectorAll('button, a[href], [role=tab], input, select, textarea')) {
          const r = el.getBoundingClientRect();
          if (r.width === 0 || r.height === 0) continue;
          let clipped = r.left < -1 || r.right > window.innerWidth + 1;
          for (let a = el.parentElement; a && !clipped; a = a.parentElement) {
            if (getComputedStyle(a).overflowX === 'visible') continue;
            const ar = a.getBoundingClientRect();
            clipped = r.left < ar.left - 1 || r.right > ar.right + 1;
          }
          if (clipped) out.push((el.getAttribute('aria-label') || (el as HTMLElement).innerText).trim());
        }
        return out;
      });
      expect(unreachable).toEqual([]);
    });
  }
});

test('on a phone, theme and language move into the account menu', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Language' })).toBeHidden();

  await page.getByRole('button', { name: 'Account settings' }).click();
  await expect(page.getByRole('menuitem', { name: 'Čeština' })).toBeVisible();
  await page.getByRole('menuitem', { name: 'Toggle theme' }).click();
  await expect(page.locator('html')).toHaveClass(/\bdark\b/);
});

test('a control revealed on hover is simply shown on a touch screen', async ({ page }) => {
  await page.goto('/media');

  /*
   * Tailwind v4 only applies `group-hover:` where the device can hover, so `opacity-0
   * group-hover:opacity-100` left these invisible on every phone and tablet. `toBeVisible`
   * does not look at opacity, so the assertion is on the computed style.
   */
  await expect(page.getByRole('button', { name: 'Actions' }).first()).toHaveCSS('opacity', '1');
});

test('past conversations are a drawer away on a phone', async ({ page, api }) => {
  api.on('GET', '/api/admin/ai/conversations', [
    {
      id: '77777777-0000-0000-0000-000000000001', title: 'Rewrite the pricing page',
      visibility: 'Private', mode: 'ask', pageArea: null, surface: 'console', siteId: null,
      branch: null, messageCount: 4, ownerUserId: 'me', mine: true,
      createdAt: '2026-09-01T08:00:00Z', updatedAt: '2026-09-01T08:05:00Z',
    },
  ]);
  await page.goto('/assistant');

  // The rail is a column at md and up; below it, it was simply not rendered.
  await page.getByRole('button', { name: 'Past conversations' }).click();
  await expect(page.getByRole('dialog').getByText('Rewrite the pricing page')).toBeVisible();
});
