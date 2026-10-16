import type { Page } from '@playwright/test';
import { test, expect } from '../fixtures/test';

/**
 * The jobs ring beside the bell (ADR 0024): it shows the average progress of what is running,
 * moves when the server pushes progress — without a reload and without a refetch — and its
 * popover cancels and retries.
 */

const RUNNING = {
  id: 'aaaaaaaa-0000-0000-0000-000000000001', tenantId: 't', tenantSlug: null, tenantName: null, parentId: null,
  kind: 'media.drive-import', lane: 'group', source: 'admin-api', titleKey: 'jobs.kinds.test.title',
  params: { count: 3 }, status: 'Running', done: 50, total: 300, stageKey: null, errorKey: null, errorDetail: null,
  linkPath: null, cancelable: true, cancelRequested: false, retryable: false, attempts: 1, maxAttempts: 3,
  actorUserId: null, createdAt: '2026-10-15T10:00:00Z', startedAt: '2026-10-15T10:00:01Z', finishedAt: null,
  updatedAt: '2026-10-15T10:00:02Z', seq: 3,
  origin: { actorKind: 'User', actorName: 'ana@example.test', service: null, traceId: null },
};

const FAILED = {
  ...RUNNING, id: 'aaaaaaaa-0000-0000-0000-000000000002', lane: 'tenant', kind: 'test.failed',
  titleKey: 'jobs.kinds.missing.title', status: 'Failed', done: 1, total: 4, cancelable: false, retryable: true,
  errorKey: 'jobs.error.failed', finishedAt: '2026-10-15T10:05:00Z', seq: 9,
  origin: { actorKind: 'System', actorName: null, service: null, traceId: null },
};

function seed(api: { on: (m: string, p: string, r: unknown) => unknown }) {
  api.on('GET', '/api/admin/jobs', ({ query }: { query: URLSearchParams }) => {
    const status = query.get('status');
    if (status === 'active') return { items: [RUNNING], nextCursor: null };
    if (status === 'recent') return { items: [FAILED], nextCursor: null };
    return { items: [RUNNING, FAILED], nextCursor: null };
  });
}

const ring = (page: Page) => page.getByTestId('jobs-indicator').getByRole('progressbar');

test('the ring shows running work and follows pushed progress without a reload or a refetch', async ({
  page, api, hub,
}) => {
  seed(api);
  await page.goto('/');
  await expect(ring(page)).toHaveAttribute('aria-valuenow', '17');
  await hub.connected;

  const before = api.requestsTo('GET', '/api/admin/jobs').length;
  hub.send('JobChanged', {
    id: RUNNING.id, tenantId: 't', parentId: null, status: 'Running', done: 150, total: 300, stageKey: null, seq: 4,
  });

  await expect(ring(page)).toHaveAttribute('aria-valuenow', '50');
  // A progress tick is patched into the cache: it must cost no request.
  await page.waitForTimeout(500);
  expect(api.requestsTo('GET', '/api/admin/jobs').length).toBe(before);
});

test('the popover lists running and finished jobs, and cancels and retries', async ({ page, api }) => {
  seed(api);
  api.on('POST', '/api/admin/jobs/:id/cancel', { status: 'cancelRequested' });
  api.on('POST', '/api/admin/jobs/:id/retry', { status: 'queued' });
  await page.goto('/');

  await page.getByTestId('jobs-indicator').click();
  const rows = page.getByTestId('job-row');
  await expect(rows).toHaveCount(2);
  // A kind this console has no text for falls back to its raw name rather than an empty row.
  await expect(rows.nth(1)).toContainText('test.failed');
  // Where each came from: a person by name, work nobody started as automatic.
  await expect(rows.nth(0)).toContainText('by ana@example.test');
  await expect(rows.nth(1)).toContainText('automatic');

  await rows.nth(0).getByRole('button', { name: 'Cancel' }).click();
  await expect.poll(() => api.requestsTo('POST', `/api/admin/jobs/${RUNNING.id}/cancel`).length).toBe(1);

  await rows.nth(1).getByRole('button', { name: 'Retry' }).click();
  await expect.poll(() => api.requestsTo('POST', `/api/admin/jobs/${FAILED.id}/retry`).length).toBe(1);
});

test('the jobs page opens a group to show its items', async ({ page, api }) => {
  seed(api);
  api.on('GET', `/api/admin/jobs/${RUNNING.id}`, {
    job: RUNNING,
    children: [
      { ...RUNNING, id: 'c1', parentId: RUNNING.id, lane: 'tenant', kind: 'child.one', titleKey: 'x.one', done: 1, total: 2 },
      { ...RUNNING, id: 'c2', parentId: RUNNING.id, lane: 'tenant', kind: 'child.two', titleKey: 'x.two', status: 'Queued' },
    ],
  });
  await page.goto('/jobs');

  await page.getByRole('button', { name: 'Items' }).first().click();
  await expect(page.getByText('child.one')).toBeVisible();
  await expect(page.getByText('child.two')).toBeVisible();
});

test('the ring fits a phone top bar', async ({ page, api }) => {
  seed(api);
  await page.setViewportSize({ width: 360, height: 740 });
  await page.goto('/');
  await expect(page.getByTestId('jobs-indicator')).toBeInViewport();
  const scroll = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(scroll).toBeLessThanOrEqual(0);
});
