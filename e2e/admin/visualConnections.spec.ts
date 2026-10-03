import { test, expect } from '../fixtures/test';
import { HOME, doc, open, saved } from '../fixtures/visualSite';

/**
 * External API connections (Mode D backlog #124): set up in Settings with a write-only key, then
 * read on a visual site like plugin content — a collection over a synced operation.
 */

const TICKETS = {
  slug: 'tickets',
  name: 'Ticket shop',
  baseUrl: 'https://api.tickets.example/v1',
  authKind: 'header',
  authName: 'X-Api-Key',
  hasSecret: true,
  operations: ['/events'],
  refreshMinutes: 60,
  refreshedAt: '2026-10-03T10:00:00Z',
  lastError: null,
  shapes: [{ operation: '/events', items: 'data', fields: ['code', 'name', 'cover'], fetchedAt: '2026-10-03T10:00:00Z' }],
};

test('a connection is added in Settings, and its key is sent once and never shown', async ({ page, api }) => {
  api.on('PUT', '/api/admin/connections/:slug', TICKETS);
  await page.goto('/settings/connections');
  await page.getByRole('button', { name: 'Add connection' }).first().click();

  await page.getByLabel('Name', { exact: true }).fill('Ticket shop');
  await page.getByLabel('Address name').fill('tickets');
  await page.getByLabel('Base URL (https)').fill('https://api.tickets.example/v1');
  await page.getByRole('combobox', { name: 'Authentication' }).click();
  await page.getByRole('option', { name: 'Key in a header' }).click();
  await page.getByLabel('Header name').fill('X-Api-Key');
  await page.getByLabel('Key', { exact: true }).fill('s3cret');
  await page.getByLabel('Operations (GET, one per line)').fill('/events\n');
  api.on('GET', '/api/admin/connections', [TICKETS]);
  await page.getByRole('button', { name: 'Save' }).click();

  await expect(page.getByText('GET /events')).toBeVisible();
  const put = api.requestsTo('PUT', '/api/admin/connections/tickets')[0]!.body;
  expect(put).toEqual({
    name: 'Ticket shop',
    baseUrl: 'https://api.tickets.example/v1',
    authKind: 'header',
    authName: 'X-Api-Key',
    secret: 's3cret',
    operations: ['/events'],
    refreshMinutes: 60,
  });

  // Editing keeps the stored key unless a new one is typed: the field starts empty.
  await page.getByTitle('Edit').click();
  await expect(page.getByLabel('Key', { exact: true })).toHaveValue('');
  await expect(page.getByLabel('Key', { exact: true })).toHaveAttribute('placeholder', 'Stored — leave empty to keep it');
});

test('a collection reads a connection’s synced operation, and binds its fields', async ({ page, api }) => {
  api
    .on('GET', '/api/admin/connections', [TICKETS])
    .on('GET', '/api/admin/sites/:id/preview/api/connections/tickets/events', {
      data: [
        { code: 'gala', name: 'Gala night' },
        { code: 'jam', name: 'Jam session' },
      ],
    });
  const frame = await open(page, api, {
    'dcms/app.json': doc({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
    [HOME]: doc({
      schemaVersion: 1,
      id: 'home',
      title: 'Home',
      root: {
        id: 'r',
        type: 'dcms.page',
        slots: {
          default: [
            { id: 'h', type: 'dcms.heading', props: { text: 'Shows' } },
            { id: 'list', type: 'dcms.collection', slots: { item: [{ id: 'title', type: 'dcms.heading', props: { text: 'Name' } }] } },
          ],
        },
      },
    }),
  });

  await frame.getByText('Choose the content this collection shows.').click();
  await page.getByRole('combobox').filter({ hasText: 'Choose content' }).click();
  await page.getByRole('option', { name: 'Ticket shop › GET /events' }).click();
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"connection": "tickets"');
  expect(JSON.parse(saved(api, HOME)!).root.slots.default[1].props.source).toEqual({ connection: 'tickets', operation: '/events', items: 'data' });

  await frame.locator('[data-dcms-node="title"] h2').click();
  await page.getByRole('combobox', { name: 'What Text shows' }).click();
  await page.getByRole('option', { name: 'The item’s name' }).click();
  await expect(frame.locator('[data-dcms-node="title"] h2')).toHaveText('Gala night');

  // The site lists every item; nothing here is reported as missing content.
  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();
  await expect(page.frameLocator('iframe[title="Preview"]').getByRole('heading', { level: 2 })).toHaveText(['Shows', 'Gala night', 'Jam session']);
  await expect(page.getByRole('button', { name: /problems?$/ })).toHaveCount(0);
});
