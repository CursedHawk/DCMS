import { platformTest as test, expect } from '../fixtures/test';
import { spillingContent, unreachableControls } from '../fixtures/layout';

/**
 * The platform console on a phone, at 360px with touch.
 *
 * <p>An operator paged at night opens this on whatever is in their pocket. What these hold it to
 * is the same contract as the admin's: every control on the screen, nothing cut off by a box
 * that clips it, and no column of a table that matters scrolled out of sight.</p>
 *
 * <p>The default fixtures answer most lists with nothing, and an empty page fits any screen. So
 * every list here gets rows, and long ones — an IPv6 range, a long email address, a failing
 * certificate — because those are what overflowed.</p>
 */
test.use({ viewport: { width: 360, height: 740 }, hasTouch: true, isMobile: true });

const user = (i: number, locked = false) => ({
  id: `00000000-0000-4000-8000-00000000000${i}`,
  email: `operator.with.a.long.address${i}@example-company.test`,
  displayName: `Ada Lovelace ${i}`,
  emailConfirmed: true,
  createdAt: '2026-01-04T09:00:00Z',
  lockedOut: locked,
  lockoutEnd: locked ? '2026-12-01T00:00:00Z' : null,
  accessFailedCount: locked ? 5 : 0,
  forgejoUsername: `ada${i}`,
  hasGitPassword: true,
  roles: i === 1 ? ['SuperAdmin', 'Support'] : [],
});

const IPV6 = '2001:db8:1234:5678::/64';

test.beforeEach(({ api }) => {
  api
    .on('GET', '/api/identity/users', {
      total: 2,
      page: 1,
      pageSize: 25,
      items: [user(1), user(2, true)],
    })
    .on('GET', '/api/platform/rate-limit-exemptions', [
      {
        id: '5b0d7d6e-0000-4000-8000-000000000001',
        cidr: IPV6,
        note: 'Load generator on the ops box',
        createdAt: '2026-09-24T12:00:00Z',
        createdBy: 'ops@example.test',
      },
    ])
    .on('GET', '/api/platform/certificates', [
      {
        id: 'c1',
        name: 'wildcard-highgeek',
        identifiers: ['*.highgeek.eu', 'highgeek.eu', 'admin.highgeek.eu'],
        enabled: true,
        requiresDns: true,
        issuer: "Let's Encrypt R11",
        notBefore: '2026-09-01T00:00:00Z',
        notAfter: '2026-11-30T00:00:00Z',
        renewedAt: '2026-09-01T00:00:00Z',
        covers: ['*.highgeek.eu', 'highgeek.eu'],
        reissueRequested: false,
        lastErrorReachedCa: true,
        lastAttemptAt: '2026-10-01T00:00:00Z',
        expired: false,
        lastError:
          'urn:ietf:params:acme:error:unauthorized: Invalid response from http://admin.highgeek.eu/.well-known/acme-challenge/x8dJ3kq0AbCdEfGhIjKlMnOpQrStUvWxYz0123456789: 404',
        daysRemaining: 51,
        issuedThisWeek: 2,
      },
    ])
    .on('GET', '/api/platform/stores', {
      stores: [
        {
          store: 'loki',
          usedBytes: 3e9,
          budgetBytes: 5e9,
          peakBytes: 4e9,
          projectedBytes: 6e9,
          growthBytesPerSecond: 1000,
          retentionSeconds: 2_592_000,
          canPurge: true,
          purgeNote: null,
        },
      ],
      dockerLogPurgeAvailable: true,
      prometheusUnreachable: false,
      awaitingStoreMetrics: false,
    })
    .on('GET', '/api/platform/roles', [
      { roleName: 'Support', permissions: ['platform:tenants:read'] },
    ])
    .on('GET', '/api/platform/permissions/catalog', {
      permissions: ['platform:tenants:read', 'platform:certificates:manage'],
      readOnly: ['platform:tenants:read'],
    })
    .on('GET', '/api/platform/health/signals', {
      reachable: true,
      services: [
        {
          service: 'admin-api',
          requestsPerSecond: 12.3,
          errorsPerSecond: 1.1,
          errorRatio: 0.09,
          p95Seconds: 0.42,
        },
      ],
      targets: [{ job: 'media-worker', instance: 'media-worker:8080', up: false }],
    });
});

const PAGES = [
  '/',
  '/tenants',
  '/users',
  '/audit',
  '/monitoring',
  '/storage',
  '/access',
  '/certificates',
  '/rate-limits',
  '/notifications',
];

for (const path of PAGES) {
  test(`nothing on ${path} sits past the edge of the screen`, async ({ page }) => {
    await page.goto(path);
    await expect(page.getByRole('button', { name: 'Account', exact: true })).toBeVisible();
    await page.waitForLoadState('networkidle');

    expect(await unreachableControls(page)).toEqual([]);
    expect(await spillingContent(page)).toEqual([]);
  });
}

test('the dialogs fit the phone they open on', async ({ page }) => {
  const opens = [
    { path: '/rate-limits', button: 'Add exemption' },
    { path: '/certificates', button: 'Add' },
    { path: '/tenants', button: 'Storage limit' },
  ];
  for (const { path, button } of opens) {
    await page.goto(path);
    await page.getByRole('button', { name: button, exact: true }).first().click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    expect(await unreachableControls(page, dialog), path).toEqual([]);
  }
});

test('the error column is on the screen, not scrolled out of it', async ({ page }) => {
  await page.goto('/monitoring');

  /*
   * The table had a 32rem minimum inside a horizontal scroller, so a phone saw service and
   * request rate and nothing said the error ratio was a swipe away — the one column that
   * answers "which service is erroring".
   */
  await expect(page.getByText('9.0%')).toBeInViewport({ ratio: 1 });
  await expect(page.getByText('420 ms')).toBeInViewport({ ratio: 1 });
});

test('a long address does not run under the button beside it', async ({ page }) => {
  await page.goto('/rate-limits');

  // The box ends before the button either way; it is the text that spilled out of it.
  const spill = await page.getByText(IPV6).evaluate((el) => el.scrollWidth - el.clientWidth);
  expect(spill).toBeLessThanOrEqual(0);
});

test('a card keeps its controls inside it', async ({ page }) => {
  await page.goto('/tenants');

  /*
   * The card's label column sized itself to "Published of total content items" on one line,
   * which left the storage cell too narrow for its own figure: the edit button hung past the
   * card's border. Still on the screen, so the edge check above cannot see it.
   */
  const card = page.getByRole('listitem').filter({ hasText: 'Acme Studio' });
  const spills = await card.evaluate((li) => {
    const edge = li.getBoundingClientRect().right;
    return [...li.querySelectorAll('button, a')]
      .filter((c) => c.getBoundingClientRect().right > edge + 1)
      .map((c) => c.getAttribute('aria-label') || (c as HTMLElement).innerText);
  });
  expect(spills).toEqual([]);
});

test('a page action drops below the title rather than squeezing it', async ({ page }) => {
  for (const [path, button] of [
    ['/rate-limits', 'Add exemption'],
    ['/certificates', 'Add'],
  ]) {
    await page.goto(path);
    const title = await page.getByRole('heading', { level: 1 }).boundingBox();
    const action = await page
      .getByRole('button', { name: button, exact: true })
      .first()
      .boundingBox();
    // Beside it, the description was a column of three words a line.
    expect(action!.y, path).toBeGreaterThan(title!.y + title!.height);
  }
});

test("a certificate's domains open the site in a new tab; a wildcard is not a site", async ({
  page,
}) => {
  await page.goto('/certificates');

  const site = page.getByRole('link', { name: 'admin.highgeek.eu' });
  await expect(site).toHaveAttribute('href', 'https://admin.highgeek.eu');
  await expect(site).toHaveAttribute('target', '_blank');
  await expect(page.getByText('*.highgeek.eu').first()).toBeVisible();
  await expect(page.getByRole('link', { name: '*.highgeek.eu' })).toHaveCount(0);
});

test('the navigation is a drawer that closes behind you', async ({ page }) => {
  await page.goto('/');
  const nav = page.getByRole('navigation', { name: 'Platform sections' });
  await expect(nav).toBeHidden();

  await page.getByRole('button', { name: 'Open navigation' }).click();
  await nav.getByRole('link', { name: 'Tenants' }).click();

  await expect(page).toHaveURL(/\/tenants$/);
  await expect(nav).toBeHidden();
});
