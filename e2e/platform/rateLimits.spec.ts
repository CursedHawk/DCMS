import { platformTest as test, expect } from '../fixtures/test';

/**
 * The rate-limit exemption list. What is worth proving from outside is the contract with
 * platform-api: the address goes up as typed (the server normalises it), a note is required
 * before the button will send anything, and removal names the row it removes.
 */

const ROW = {
  id: '5b0d7d6e-0000-4000-8000-000000000001',
  cidr: '203.0.113.7/32',
  note: 'Load generator on the ops box',
  createdAt: '2026-09-24T12:00:00Z',
  createdBy: 'ops@example.test',
};

test('lists exemptions, adds one with a reason, and removes one', async ({ page, api }) => {
  api
    .on('GET', '/api/platform/rate-limit-exemptions', [ROW])
    .on('POST', '/api/platform/rate-limit-exemptions', ({ body }) => ({
      ...ROW,
      id: '5b0d7d6e-0000-4000-8000-000000000002',
      cidr: '198.51.100.0/24',
      note: (body as { note: string }).note,
    }))
    .on('DELETE', '/api/platform/rate-limit-exemptions/:id', {});

  await page.goto('/rate-limits');
  await expect(page.getByText('203.0.113.7/32')).toBeVisible();
  await expect(page.getByText('Load generator on the ops box')).toBeVisible();

  await page.getByRole('button', { name: 'Add exemption' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Address or range').fill('198.51.100.9/24');
  // No reason, no request: an unexplained hole in a DoS control is the one nobody removes.
  await expect(dialog.getByRole('button', { name: 'Exempt' })).toBeDisabled();
  await dialog.getByLabel('Why').fill('Partner egress range');
  await dialog.getByRole('button', { name: 'Exempt' }).click();
  await expect(page.getByText('198.51.100.0/24 is exempt from rate limiting.')).toBeVisible();

  const posted = api.requests.find((r) => r.method === 'POST' && r.path === '/api/platform/rate-limit-exemptions');
  expect(posted?.body).toEqual({ address: '198.51.100.9/24', note: 'Partner egress range' });

  await page.getByRole('button', { name: 'Remove exemption for 203.0.113.7/32' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Remove exemption' }).click();
  await expect(page.getByText('203.0.113.7/32 is rate-limited again.')).toBeVisible();
  expect(api.requests.some((r) => r.method === 'DELETE' && r.path === `/api/platform/rate-limit-exemptions/${ROW.id}`)).toBe(true);
});

test.describe('an operator without platform:ratelimits:manage', () => {
  test.use({ grants: ['platform:overview:read'] });

  test('does not see the page in the sidebar', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('navigation', { name: 'Platform sections' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Rate limits' })).toHaveCount(0);
  });
});
