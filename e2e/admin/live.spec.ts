import type { Page } from '@playwright/test';
import { test, expect, data } from '../fixtures/test';

/**
 * The brief's "auto-refresh when the server does something".
 *
 * <p>This is the one behaviour that only an end-to-end test can claim. The tag → query map has
 * unit tests and the hub has server-side integration tests, but neither answers the question
 * the feature exists for: does a message arriving on the socket actually repaint an open page,
 * without the reader doing anything and without the page reloading?</p>
 *
 * <p>Every test here stamps `window` before the push and reads the stamp afterwards. A reload
 * clears it, so the stamp surviving is the proof that the update was live rather than a
 * navigation that happened to fetch fresh data.</p>
 */

const mark = (page: Page) =>
  page.evaluate(() => {
    (window as unknown as { __live: number }).__live = 1;
  });

const sameDocument = (page: Page) =>
  page.evaluate(() => (window as unknown as { __live?: number }).__live === 1);

test('a media change pushed by the server updates the open grid', async ({ page, api, hub }) => {
  await page.goto('/media');
  await expect(page.getByText('logo.png')).toBeVisible();
  await expect(page.getByText('poster.png')).toHaveCount(0);
  await hub.connected;
  await mark(page);

  // What the server now holds. Nothing has told the browser yet.
  api.on('GET', '/api/admin/media', [
    ...data.MEDIA_ASSETS,
    {
      id: 'dddddddd-0000-0000-0000-00000000000a', category: 'Image', fileName: 'poster.png',
      status: 'Ready', sizeBytes: 4096, folderId: null, createdAt: '2026-09-08T10:00:00Z',
      variantBytes: 1024, variantCount: 1, width: 800, height: 600,
    },
  ]);

  hub.resourceChanged('media');

  await expect(page.getByText('poster.png')).toBeVisible();
  expect(await sameDocument(page)).toBe(true);
});

test('an unmapped tag costs the page nothing', async ({ page, api, hub }) => {
  // A console can be older than the server it talks to. An unknown tag must be ignored rather
  // than treated as "everything is stale", or one new server-side tag turns every open console
  // into a refetch storm.
  await page.goto('/media');
  await expect(page.getByText('logo.png')).toBeVisible();
  await hub.connected;

  const before = api.requestsTo('GET', '/api/admin/media').length;
  hub.resourceChanged('quotas-and-billing');
  await page.waitForTimeout(500);

  expect(api.requestsTo('GET', '/api/admin/media').length).toBe(before);
});

test('a notification pushed to this user reaches the bell without a reload', async ({
  page,
  api,
  hub,
}) => {
  await page.goto('/');
  await hub.connected;
  await mark(page);

  const arrived = {
    ...data.NOTIFICATIONS.items[0],
    id: 'ffffffff-0000-0000-0000-00000000000a',
    severity: 'Error',
    // Somebody else's action. The toast rule suppresses your own Info and Success, and always
    // interrupts for an Error.
    actorUserId: '22222222-2222-2222-2222-222222222222',
    createdAt: '2026-09-08T12:00:00Z',
  };
  api.on('GET', '/api/admin/notifications', {
    items: [arrived, ...data.NOTIFICATIONS.items],
    unreadCount: 2,
    nextCursor: null,
  });

  hub.send('Notification', arrived);

  await expect(page.getByRole('button', { name: /2 unread/ })).toBeVisible();
  expect(await sameDocument(page)).toBe(true);
});

test('a dropped socket reconnects and refetches, because nothing replays what was missed', async ({
  page,
  api,
  hub,
}) => {
  await page.goto('/media');
  await expect(page.getByText('logo.png')).toBeVisible();
  await hub.connected;

  const before = api.requestsTo('GET', '/api/admin/media').length;

  // The hub holds no backlog. A console that was disconnected has been looking at data that
  // could have changed for the length of the outage, so reconnecting invalidates the whole
  // cache rather than only the bell — that is what this asserts, and it is the reason the
  // slow polls were lengthened rather than deleted.
  await hub.drop();

  await expect
    .poll(() => api.requestsTo('GET', '/api/admin/media').length, { timeout: 15_000 })
    .toBeGreaterThan(before);
});
