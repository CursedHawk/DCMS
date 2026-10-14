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

test('the order is asked of the server, and the list keeps its filters in the URL', async ({ page, api }) => {
  await openCollection(page);
  const table = page.getByRole('table');

  await table.getByRole('button', { name: 'Sort by Title' }).click();
  await expect.poll(() => api.requestsTo('GET', '/api/admin/content/page').some(
    (r) => r.query.get('sort') === 'title' && r.query.get('dir') === 'asc')).toBe(true);
  await table.getByRole('button', { name: 'Sort by Title' }).click();
  await expect.poll(() => api.requestsTo('GET', '/api/admin/content/page').some(
    (r) => r.query.get('sort') === 'title' && r.query.get('dir') === 'desc')).toBe(true);

  // The rows are drawn in the order the server answered, not re-sorted in the browser: the
  // list is paged, and sorting what has loaded would order only that.
  const titles = await table.getByRole('row').locator('td:nth-child(2)').allTextContents();
  expect(titles).toEqual([...titles].sort((a, b) => b.localeCompare(a)));

  await page.getByPlaceholder('Search by title or slug…').fill('launch');
  await expect(page).toHaveURL(/q=launch/);
  await expect(page).toHaveURL(/sort=title/);

  // A reload — or a link someone shared — lands on the same list.
  await page.reload();
  await expect(page.getByPlaceholder('Search by title or slug…')).toHaveValue('launch');
  await expect(page.getByRole('cell', { name: 'Launch notes' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Hello, world' })).toHaveCount(0);
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

test('a new item is given a generated slug: title, else media, then an id, until the author writes one', async ({
  page,
  api,
}) => {
  api.on('POST', '/api/admin/content', { id: 'new-item' });
  await openCollection(page);
  await page.getByRole('button', { name: 'New item' }).first().click();
  const dialog = page.getByRole('dialog');
  const slug = dialog.getByLabel('Slug');
  // The type's fields come first, the generated slug last: the title is the first textbox.
  const title = dialog.getByRole('textbox').first();

  // Before anything is written the slug is already there: the item's random id.
  await expect(slug).toHaveValue(/^[a-z0-9]{8}$/);
  const id = (await slug.inputValue()).trim();

  // A picked file names an untitled item.
  await dialog.getByRole('button', { name: 'Select media' }).click();
  await page.getByRole('button', { name: /logo\.png/ }).click();
  await expect(slug).toHaveValue(`logo-${id}`);

  // The title wins over the file, and the id stays the same.
  await title.fill('Příliš žluťoučký kůň');
  await expect(slug).toHaveValue(`prilis-zlutoucky-kun-${id}`);

  // What the author types is theirs.
  await slug.fill('kun');
  await title.fill('Something else');
  await expect(slug).toHaveValue('kun');

  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/content').at(-1)?.body).toMatchObject({ slug: 'kun' });
});

test('a taken slug is explained at the field, and the filled form stays to fix it', async ({ page, api }) => {
  let attempts = 0;
  api.on('POST', '/api/admin/content', ({ route }) => {
    if (attempts++ === 0) {
      void route.fulfill({
        status: 409,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'Slug already exists for this content type.' }),
      });
      return undefined;
    }
    return { id: 'new-item' };
  });
  await openCollection(page);
  await page.getByRole('button', { name: 'New item' }).first().click();
  const dialog = page.getByRole('dialog');
  const slug = dialog.getByLabel('Slug');
  const title = dialog.getByRole('textbox').first();

  await title.fill('Hello, world');
  await slug.fill('hello-world');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(dialog.getByRole('alert')).toHaveText(
    '“hello-world” is already in use, and a slug has to be unique. Change it and try again.',
  );
  await expect(slug).toBeFocused();
  await expect(slug).toHaveAttribute('aria-invalid', 'true');
  await expect(title).toHaveValue('Hello, world');

  await slug.fill('hello-world-2');
  await expect(dialog.getByRole('alert')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(api.requestsTo('POST', '/api/admin/content').at(-1)?.body).toMatchObject({ slug: 'hello-world-2' });
});

test('a view filters on a field, shows it as a column, and is saved and shared', async ({ page, api }) => {
  let saved: Record<string, unknown>[] = [];
  api
    .on('GET', '/api/admin/views', () => ({ views: saved, defaultView: null }))
    .on('POST', '/api/admin/views', ({ body }) => {
      const view = { id: 'v1', mine: true, sharedWithWorkspace: false, sharedWith: { userIds: [], roleIds: [] }, ...(body as object) };
      saved = [view];
      return view;
    })
    .on('PUT', '/api/admin/views/:id/sharing', ({ body }) => ({ ...saved[0], sharedWithWorkspace: (body as { workspace: boolean }).workspace }))
    .on('GET', '/api/admin/views/audience', {
      people: [{ id: 'u2', label: 'colleague@acme.test' }],
      roles: [{ id: 'r1', label: 'Editor' }],
    });
  await openCollection(page);

  // Filter: tags contains "launch", asked of the server as the view's filter tree.
  await page.getByRole('button', { name: 'Filters' }).click();
  await page.getByRole('button', { name: 'Add condition' }).click();
  await page.getByRole('combobox', { name: 'Field' }).click();
  await page.getByRole('option', { name: 'Tags' }).click();
  await page.getByRole('textbox', { name: 'Value' }).fill('launch');
  await expect.poll(() =>
    api.requestsTo('GET', '/api/admin/content/page').some((r) => {
      const f = r.query.get('filter');
      return !!f && JSON.stringify(JSON.parse(f)) === JSON.stringify({ and: [{ field: 'tags', op: 'contains', value: 'launch' }] });
    })).toBe(true);

  // A field as a column.
  await page.getByRole('button', { name: 'Columns' }).click();
  await page.getByRole('checkbox', { name: 'Show Tags' }).click();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('columnheader', { name: /Tags/ })).toBeVisible();
  await expect.poll(() => api.requestsTo('GET', '/api/admin/content/page').some((r) => r.query.get('columns') === 'tags')).toBe(true);

  // Saved as a personal view, for this collection.
  await page.getByRole('button', { name: 'Save as…' }).click();
  await page.getByLabel('Name').fill('Launch posts');
  await page.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/views').length).toBe(1);
  const [create] = api.requestsTo('POST', '/api/admin/views');
  expect(create.body).toMatchObject({
    table: expect.stringMatching(/^content:[a-z0-9-]+:post$/),
    name: 'Launch posts',
    definition: { columns: ['title', 'slug', 'status', 'updated', 'tags'], filter: { and: [{ field: 'tags', op: 'contains', value: 'launch' }] } },
  });
  await expect(page.getByRole('button', { name: 'View: Launch posts' })).toBeVisible();
  await expect(page).toHaveURL(/view=v1/);

  // Shared with the whole workspace.
  await page.getByRole('button', { name: 'View actions' }).click();
  await page.getByRole('menuitem', { name: 'Share…' }).click();
  await page.getByRole('dialog').getByRole('checkbox', { name: 'Everyone in the workspace' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await expect.poll(() => api.requestsTo('PUT', '/api/admin/views/v1/sharing').map((r) => r.body))
    .toEqual([{ workspace: true, userIds: [], roleIds: [] }]);
});
