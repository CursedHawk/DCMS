import { test, expect, data } from '../fixtures/test';

/** Opens the collection the fixtures provide, which is where every list assertion starts. */
async function openCollection(page: import('@playwright/test').Page) {
  await page.goto('/content');
  await expect(page.getByRole('cell', { name: 'Hello, world' })).toBeVisible();
}

test('the list asks the server to filter, rather than filtering what it already has', async ({
  page,
  api,
}) => {
  await openCollection(page);

  await page.getByPlaceholder('Search by title or slug…').fill('launch');

  await expect(page.getByRole('cell', { name: 'Launch notes' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Hello, world' })).toHaveCount(0);

  /*
   * The point of the change this covers. The list used to fetch every item in the collection
   * WITH its full draft — the only way the browser could show a real title — and filter locally,
   * so the payload grew with what authors had written rather than with the row count. The search
   * now goes to the server, and no draft comes back.
   */
  const search = api
    .requestsTo('GET', '/api/admin/content/page')
    .find((r) => r.query.get('search') === 'launch');
  expect(search, 'the search reached the server').toBeTruthy();
});

test('typing is a pause, not a request per keystroke', async ({ page, api }) => {
  await openCollection(page);
  const before = api.requestsTo('GET', '/api/admin/content/page').length;

  await page.getByPlaceholder('Search by title or slug…').pressSequentially('launch', { delay: 30 });
  await expect(page.getByRole('cell', { name: 'Launch notes' })).toBeVisible();

  // Six characters, well under six requests: without the debounce the answers race and the list
  // flickers through the results of prefixes nobody asked about.
  const after = api.requestsTo('GET', '/api/admin/content/page').length - before;
  expect(after).toBeLessThan(4);
});

test('a selection can be published, and the server is asked once per item', async ({
  page,
  api,
}) => {
  await openCollection(page);

  await page.getByRole('checkbox', { name: 'Select all' }).click();
  await expect(page.getByText('2 selected')).toBeVisible();

  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(page.getByText('2 items published.')).toBeVisible();

  /*
   * One request per item, on purpose. There is no bulk endpoint: each item is its own audit
   * record, its own outbox row and its own permission check, and a server-side batch would have
   * to reproduce all three while making a partial failure invisible.
   */
  const published = api.requests.filter((r) => r.path.endsWith('/publish'));
  expect(published.map((r) => r.path.split('/').at(-2))).toEqual(
    expect.arrayContaining(data.CONTENT_ROWS.map((r) => r.id)),
  );
});

test('a partial failure is reported as a partial failure', async ({ page, api }) => {
  await openCollection(page);

  // One of the two refuses. A green tick that is half true is the failure mode this exists to
  // avoid: the reader would go away believing both went out.
  api.on('POST', '/api/admin/content/:id/publish', ({ params, route }) => {
    if (params.id !== data.CONTENT_ROWS[0].id) {
      void route.fulfill({
        status: 409,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'Nothing to publish.' }),
      });
      return undefined;
    }
    return { status: 'published' };
  });

  await page.getByRole('checkbox', { name: 'Select all' }).click();
  await page.getByRole('button', { name: 'Publish', exact: true }).click();

  await expect(page.getByText('1 done, 1 refused.')).toBeVisible();
});

test('the publishing queue spans collections and shows what is queued', async ({ page }) => {
  await page.goto('/content');
  await page.getByRole('tab', { name: 'Scheduled' }).click();

  await expect(page.getByRole('heading', { name: 'Publishing queue' })).toBeVisible();
  // The title of the version that is QUEUED, not of the draft being written now — an author who
  // has kept typing since scheduling should still see the headline that is actually going out.
  await expect(page.getByRole('cell', { name: 'Launch notes, as approved' })).toBeVisible();
  await expect(page.getByRole('cell', { name: /Press room/ })).toBeVisible();
});

test('a queued publish can be cancelled from the queue', async ({ page, api }) => {
  await page.goto('/content');
  await page.getByRole('tab', { name: 'Scheduled' }).click();
  await expect(page.getByRole('cell', { name: 'Launch notes, as approved' })).toBeVisible();

  api.on('GET', '/api/admin/content/scheduled', { items: [] });
  await page.getByRole('button', { name: 'Cancel' }).click();

  await expect(page.getByText('Nothing is scheduled')).toBeVisible();
  expect(api.requests.filter((r) => r.method === 'DELETE' && r.path.endsWith('/schedule')))
    .toHaveLength(1);
});

test('the types and tags views read data the page already had', async ({ page, api }) => {
  await page.goto('/content');

  await page.getByRole('tab', { name: 'Types' }).click();
  // Scoped to the table: the content type also names itself in the field list beside it.
  await expect(page.getByRole('table').getByText('post')).toBeVisible();

  // The catalogue was fetched once, for the collection rail; the Types view is a second reading
  // of it rather than a second request.
  expect(api.requestsTo('GET', '/api/admin/plugins/catalog').length).toBeLessThanOrEqual(1);
});
