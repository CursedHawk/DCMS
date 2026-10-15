import { platformTest as test, expect } from '../fixtures/test';

/**
 * The console's view of background jobs (ADR 0024): every tenant's work in one ring and one
 * page, with each queue's backlog — including a queue nobody drains, the missing-worker case.
 */

const JOB = {
  id: 'bbbbbbbb-0000-0000-0000-000000000001', tenantId: '11111111-1111-1111-1111-111111111111',
  tenantSlug: 'acme', tenantName: 'Acme', parentId: null, kind: 'media.drive-import', lane: 'tenant',
  source: 'admin-api', titleKey: 'x', params: {}, status: 'Running', done: 1, total: 4, stageKey: null,
  errorKey: null, errorDetail: null, linkPath: null, cancelable: true, cancelRequested: false, retryable: false,
  attempts: 1, maxAttempts: 3, actorUserId: null, createdAt: '2026-10-15T10:00:00Z', startedAt: null,
  finishedAt: null, updatedAt: '2026-10-15T10:00:00Z', seq: 2,
};

test('the console shows every tenant’s jobs and follows pushed progress', async ({ page, api, hub }) => {
  api.on('GET', '/api/platform/jobs', ({ query }: { query: URLSearchParams }) =>
    query.get('status') === 'finished' ? { items: [], nextCursor: null } : { items: [JOB], nextCursor: null });
  await page.goto('/');

  const ring = page.getByTestId('jobs-indicator').getByRole('progressbar');
  await expect(ring).toHaveAttribute('aria-valuenow', '25');
  await hub.connected;
  hub.send('JobChanged', { ...JOB, done: 3, seq: 3 });
  await expect(ring).toHaveAttribute('aria-valuenow', '75');

  await page.getByTestId('jobs-indicator').click();
  await expect(page.getByTestId('job-row')).toContainText('acme · Media drive import');
});

test('the jobs page shows backlog per tenant and a queue with no worker', async ({ page, api }) => {
  api.on('GET', '/api/platform/jobs/overview', {
    tenants: [{ tenantId: JOB.tenantId, tenantSlug: 'acme', tenantName: 'Acme', queued: 2, running: 1, failedLast24h: 0 }],
    queues: [
      { stream: 'MEDIA', consumer: 'media-image', pending: 0, ackPending: 1 },
      { stream: 'JOBS', consumer: '', pending: 7, ackPending: 0 },
    ],
  });
  await page.goto('/jobs');

  await expect(page.getByRole('heading', { name: 'Background jobs' })).toBeVisible();
  await expect(page.getByText('nobody').first()).toBeVisible();
  await expect(page.getByText('Acme').first()).toBeVisible();
});

test('an operator without platform:jobs:read gets no ring', async ({ page, api }) => {
  api.on('GET', '/api/platform/me', {
    userId: 'u', name: 'Support', email: 's@example.test', roles: ['Support'], isSuperAdmin: false,
    permissions: ['platform:overview:read'],
  });
  await page.goto('/');
  await expect(page.getByRole('heading').first()).toBeVisible();
  await expect(page.getByTestId('jobs-indicator')).toHaveCount(0);
});
