import { test, expect } from '../fixtures/test';
import { mainOverflow, spillingContent, unreachableControls, useLongStrings } from '../fixtures/layout';

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

  // Measured on `main`, not the document: the shell clips the document, so a page that is too
  // wide scrolls sideways inside `main` and the document never notices.
  expect(await mainOverflow(page)).toBeLessThanOrEqual(1);
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
  '/', '/content', '/media', '/sites', '/plugins', '/apps/press-room/content/post', '/marketplace',
  '/marketplace/forms', '/notifications', '/settings/members', '/settings/roles',
  '/settings/domains', '/settings/usage', '/apps/contact/inbox',
  '/account', '/assistant', '/jobs', '/settings/general', '/settings/ai', '/settings/audit',
  '/settings/api', '/settings/connections', '/apps/press-room/data',
  '/apps/press-room/settings', '/apps/press-room/integration',
  '/apps/press-room/activity',
];

test.describe('at 360px', () => {
  test.use({ viewport: { width: 360, height: 740 } });

  for (const path of PAGES) {
    test(`nothing on ${path} sits past the edge of the screen`, async ({ page }) => {
      await page.goto(path);
      await expect(page.getByRole('button', { name: 'Your account' })).toBeVisible();
      // The page itself, not the shell around its lazy chunk: measured before this, a sweep
      // could pass on a spinner.
      await expect(page.locator('main h1').first()).toBeVisible();
      await page.waitForLoadState('networkidle');

      expect(await unreachableControls(page)).toEqual([]);
      expect(await spillingContent(page)).toEqual([]);
      expect(await mainOverflow(page)).toBeLessThanOrEqual(1);
    });
  }
});

/*
 * The same sweep with the names people actually type. A site called "Acme" fits any layout; one
 * called after a quarterly report does not, and that is the one that pushed a header's buttons off
 * the screen.
 */
test.describe('at 360px with long names', () => {
  test.use({ viewport: { width: 360, height: 740 } });

  for (const path of PAGES) {
    test(`long names on ${path} stay inside their boxes`, async ({ page, api }) => {
      useLongStrings(api);
      await page.goto(path);
      await expect(page.getByRole('button', { name: 'Your account' })).toBeVisible();
      // The page itself, not the shell around its lazy chunk: measured before this, a sweep
      // could pass on a spinner.
      await expect(page.locator('main h1').first()).toBeVisible();
      await page.waitForLoadState('networkidle');

      expect(await unreachableControls(page)).toEqual([]);
      expect(await spillingContent(page)).toEqual([]);
      expect(await mainOverflow(page)).toBeLessThanOrEqual(1);
    });
  }
});

test('on a phone, theme and language move into the account menu', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Language' })).toBeHidden();

  await page.getByRole('button', { name: 'Your account' }).click();
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

test('a long provisioned domain wraps, and opens the site in a new tab', async ({ page, api }) => {
  const host = 'quarterly-investor-relations-portal-acme-studio.dcms.highgeek.eu';
  const custom = 'www.a-very-long-custom-domain-name-for-acme-studio.example';
  api.on('GET', '/api/admin/domains', [
    {
      id: 'd1', hostname: host, verified: true, isPrimary: true, managed: true, siteId: null,
      siteName: null, txtRecord: '', txtValue: '',
    },
    {
      id: 'd2', hostname: custom, verified: false, isPrimary: false, managed: false, siteId: null,
      siteName: null, txtRecord: `_dcms-verify.${custom}`, txtValue: 'dcms-verify=0123456789abcdef0123456789abcdef',
    },
    {
      id: 'd3', hostname: 'moordoor.cz', verified: true, isPrimary: false, managed: false, siteId: null,
      siteName: null, txtRecord: '', txtValue: '',
    },
  ]);
  // The CA's own sentence, with a 43-character challenge token in it: one word wider than the box.
  api.on('GET', '/api/admin/domains/certificates', [
    {
      hostname: 'moordoor.cz', source: 'dcms', issuer: "(STAGING) Let's Encrypt", notBefore: '2026-09-01T00:00:00Z',
      notAfter: '2026-11-30T00:00:00Z', renewedAt: null, reissueRequested: false,
      lastError:
        'urn:ietf:params:acme:error:unauthorized: Invalid response from http://moordoor.cz/.well-known/acme-challenge/x8dJ3kq0AbCdEfGhIjKlMnOpQrStUvWxYz0123456789: 404',
      lastAttemptAt: '2026-10-10T08:00:00Z', consecutiveFailures: 3, expired: false, daysRemaining: 51,
    },
  ]);
  await page.setViewportSize({ width: 360, height: 740 });
  await page.goto('/settings/domains');

  // One unbroken word wider than the phone: it ran off the right edge as plain text.
  const link = page.getByRole('link', { name: host });
  await expect(link).toHaveAttribute('href', `https://${host}`);
  await expect(link).toHaveAttribute('target', '_blank');
  await expect(link).toHaveAttribute('rel', /noopener/);
  // Below the Settings list on a phone; what matters is that it fits once it is on screen.
  await link.scrollIntoViewIfNeeded();
  await expect(link).toBeInViewport({ ratio: 1 });
  const record = page.getByText(`_dcms-verify.${custom}`);
  await record.scrollIntoViewIfNeeded();
  await expect(record).toBeInViewport({ ratio: 1 });
  // Beside its label and the copy button, the value was a column a few characters wide.
  const value = await page.getByText('dcms-verify=0123456789abcdef0123456789abcdef').boundingBox();
  expect(value!.width).toBeGreaterThan(140);

  expect(await unreachableControls(page)).toEqual([]);
  expect(await spillingContent(page)).toEqual([]);
  const sideways = await page.locator('main').first().evaluate((m) => m.scrollWidth - m.clientWidth);
  expect(sideways).toBeLessThanOrEqual(0);
});

test.describe('at 360px, the rest', () => {
  test.use({ viewport: { width: 360, height: 740 } });

  test.describe('as a platform administrator', () => {
    test.use({ superAdmin: true });
    test('nothing on /tenants sits past the edge of the screen', async ({ page }) => {
      await page.goto('/tenants');
      await expect(page.locator('main h1').first()).toBeVisible();
      await page.waitForLoadState('networkidle');
      expect(await unreachableControls(page)).toEqual([]);
      expect(await spillingContent(page)).toEqual([]);
      expect(await mainOverflow(page)).toBeLessThanOrEqual(1);
    });
  });

  test('a static-files site fits, under the editor header', async ({ page, api }) => {
    api
      .on('GET', '/api/admin/sites/:id', {
        id: '22222222-2222-2222-2222-222222222222', name: 'QuarterlyInvestorRelationsLandingPageArchive2026',
        renderMode: 'StaticFiles', staticBundle: null,
      })
      .on('GET', '/api/admin/sites/:id/builds', []);
    await page.goto('/sites/22222222-2222-2222-2222-222222222222');
    // Focus mode: no app sidebar, but always a way back.
    await expect(page.getByRole('link', { name: 'Back to Sites' })).toBeVisible();
    await page.waitForLoadState('networkidle');
    expect(await unreachableControls(page)).toEqual([]);
    expect(await spillingContent(page)).toEqual([]);
  });

  /*
   * A dialog that fits its page can still not fit the phone: a two-column field grid, a footer
   * that does not wrap. Opened where people open them, then measured inside the dialog.
   */
  test('the dialogs fit the phone they open on', async ({ page }) => {
    const opens = [
      { path: '/settings/members', button: 'Invite member' },
      { path: '/settings/roles', button: 'Create role' },
      { path: '/settings/domains', button: 'Add domain' },
      { path: '/sites', button: 'New site' },
      { path: '/content', button: 'New item' },
      { path: '/plugins?add=dcms.blog', button: null },
    ];
    for (const { path, button } of opens) {
      await page.goto(path);
      if (button) await page.getByRole('button', { name: button, exact: true }).first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      expect(await unreachableControls(page, dialog), path).toEqual([]);
      expect(await spillingContent(page), path).toEqual([]);
    }
  });

  /*
   * Czech labels run 10–30% longer than English, and every fixed width in the console was
   * chosen looking at English. Same sweep, the other language, the long names on.
   */
  test.describe('in Czech', () => {
    for (const path of PAGES) {
      test(`${path} fits in Czech`, async ({ page, api }) => {
        useLongStrings(api);
        await page.addInitScript(() => {
          try {
            localStorage.setItem('dcms.lang', 'cs');
          } catch {
            /* the sweep then runs in English, and says so by failing on the Czech label below */
          }
        });
        await page.goto(path);
        await expect(page.getByRole('button', { name: 'Váš účet' })).toBeVisible();
        await expect(page.locator('main h1').first()).toBeVisible();
        await page.waitForLoadState('networkidle');
        expect(await unreachableControls(page)).toEqual([]);
        expect(await spillingContent(page)).toEqual([]);
        expect(await mainOverflow(page)).toBeLessThanOrEqual(1);
      });
    }
  });
});
