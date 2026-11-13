import { test, expect } from '../fixtures/test';

/**
 * The AI knowledge store's console page (ADR 0034): what the agents saved, by folder, and what a
 * member corrects or prunes. admin-api does the storing; this proves what the page sends.
 */
const ENTRIES = [
  {
    id: 'k1',
    siteId: null,
    path: 'decisions/hero-layout',
    title: 'Hero layout',
    description: 'Cards, not a carousel',
    source: 'agent',
    updatedAt: '2026-11-13T10:00:00Z',
  },
  {
    id: 'k2',
    siteId: null,
    path: 'conversations/2026-11-12-hr-portal-1a2b3c4d/part-1',
    title: 'HR portal — part 1',
    description: 'Archive of the first part of the HR portal conversation',
    source: 'compaction',
    updatedAt: '2026-11-12T12:00:00Z',
  },
];

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/ai/knowledge', { folder: '', folders: [], entries: ENTRIES })
    .on('GET', '/api/admin/ai/knowledge/entry', {
      ...ENTRIES[0],
      body: 'We chose **cards** because the carousel hid half the offers.',
      conversationId: null,
      fromSeq: null,
      toSeq: null,
      createdAt: '2026-11-13T10:00:00Z',
    })
    .on('PUT', '/api/admin/ai/knowledge/entry', ({ body }) => ({ ...(body as object), id: 'k1', updatedAt: '2026-11-13T11:00:00Z' }))
    .on('DELETE', '/api/admin/ai/knowledge/:id', {});
});

test('a member reads what an agent decided, by folder, and corrects it', async ({ page, api }) => {
  await page.goto('/settings/knowledge');
  await expect(page.getByRole('heading', { name: 'AI knowledge' })).toBeVisible();

  // Folders come from the paths.
  await page.getByRole('navigation', { name: 'Folders' }).getByRole('button', { name: /decisions/ }).click();
  await expect(page.getByText('HR portal — part 1')).toHaveCount(0);

  await page.getByRole('button', { name: /Hero layout/ }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('carousel hid half the offers')).toBeVisible();

  await dialog.getByRole('button', { name: 'Edit' }).click();
  await dialog.getByLabel('One-line description').fill('Cards, three per row');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Entry saved')).toBeVisible();

  const put = api.requestsTo('PUT', '/api/admin/ai/knowledge/entry').at(-1);
  expect(put?.body).toMatchObject({
    siteId: null,
    path: 'decisions/hero-layout',
    description: 'Cards, three per row',
    source: 'member',
    expectedUpdatedAt: '2026-11-13T10:00:00Z',
  });
});

test('a member deletes an entry the agents should no longer find', async ({ page, api }) => {
  await page.goto('/settings/knowledge');
  await page.getByRole('button', { name: 'Delete decisions/hero-layout' }).click();
  await page.getByRole('dialog').getByRole('button', { name: /Delete|Confirm/ }).last().click();
  await expect(page.getByText('Entry deleted')).toBeVisible();
  expect(api.requestsTo('DELETE', '/api/admin/ai/knowledge/k1')).toHaveLength(1);
});
