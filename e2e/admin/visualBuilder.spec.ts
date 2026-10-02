import type { Locator, Page } from '@playwright/test';
import type { MockApi } from '../fixtures/api';
import { test, expect } from '../fixtures/test';
import { API_MANIFEST, HOME, IDE_FILES, RUNTIME_MANIFEST, SITE, builderServer, doc, open, saved } from '../fixtures/visualSite';

/**
 * The Mode D builder: React components on a GrapesJS canvas (ADR 0020).
 *
 * <p>This is the go/no-go spike's gate in a real browser — the part jsdom cannot see. The
 * components must be drawn by React *inside* the canvas frame, clicking one must select it,
 * an inspector edit must reach the page file the draft saves, and undo must take it back.
 * What is asserted is the draft save — the JSON that actually leaves the browser.</p>
 */

function site(api: MockApi) {
  builderServer(api);
}

/** The last saved content of the home page, as the file text the builder wrote. */
function savedHome(api: MockApi): string | null {
  const puts = api
    .requestsTo('PATCH', IDE_FILES)
    .map((r) => (r.body as { put?: Record<string, { content?: string } | string> }).put?.[HOME])
    .filter((v) => v !== undefined);
  const last = puts.at(-1);
  if (last === undefined) return null;
  return typeof last === 'string' ? last : (last.content ?? null);
}

/**
 * Drag with the real mouse, in small steps. GrapesJS's sorter tracks the pointer across the
 * canvas frame and decides the drop position as it goes; `locator.dragTo` jumps straight to the
 * target and the sorter never sees it arrive (and synthetic DragEvents never start it at all).
 */
async function dragWithMouse(page: Page, from: Locator, to: Locator) {
  const a = (await from.boundingBox())!;
  const b = (await to.boundingBox())!;
  const start = { x: a.x + a.width / 2, y: a.y + a.height / 2 };
  // Well inside the target: within ~10px of a container's edge the sorter deliberately aims
  // at the container's parent instead, which is how an author drops *beside* a section.
  const end = { x: b.x + 40, y: b.y + b.height / 3 };
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  for (let i = 1; i <= 20; i++) {
    await page.mouse.move(start.x + ((end.x - start.x) * i) / 20, start.y + ((end.y - start.y) * i) / 20);
  }
  // The sorter places its marker on an animation frame; releasing before it has is a drop
  // onto nothing.
  await page.waitForTimeout(250);
  await page.mouse.up();
}

test('draws React components in the canvas, edits them, saves the page and undoes', async ({ page, api }) => {
  site(api);
  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);

  const frame = page.frameLocator('iframe.gjs-frame');
  // The starter's heading carries the site's name — drawn by the runtime's own Heading.
  const heading = frame.locator('[data-dcms-type="dcms.heading"] h1');
  await expect(heading).toHaveText('Acme', { timeout: 30000 });
  // Every node and slot is a real wrapper, exactly as on the published site.
  await expect(frame.locator('.dcms-node[data-dcms-node="hero"] .dcms-slot').first()).toBeVisible();
  await expect(frame.locator('.dcms-slot.dcms-stack-horizontal')).toBeVisible();

  // The new site was seeded into the draft: the documents and the scaffold the server sent.
  await expect.poll(() => savedHome(api) !== null, { timeout: 15000 }).toBe(true);
  const seeded = api.requestsTo('PATCH', IDE_FILES).flatMap((r) => Object.keys((r.body as { put?: object }).put ?? {}));
  expect(seeded).toEqual(expect.arrayContaining(['dcms/app.json', 'dcms/theme.json', 'src/main.tsx', 'src/dcms/runtime/index.ts']));

  // Select the heading on the canvas: the inspector shows its props.
  await heading.click();
  const text = page.getByLabel('Text', { exact: true });
  await expect(text).toHaveValue('Acme');

  await text.fill('Hello Mode D');
  await text.press('Enter');
  await expect(heading).toHaveText('Hello Mode D');
  await expect
    .poll(() => savedHome(api), { timeout: 15000 })
    .toContain('"text": "Hello Mode D"');

  // Undo is GrapesJS's own, and the React view follows it.
  await page.getByTitle('Undo').click();
  await expect(heading).toHaveText('Acme');
  await expect(text).toHaveValue('Acme');
  await expect.poll(() => savedHome(api), { timeout: 15000 }).toContain('"text": "Acme"');
});

test('a component dragged from the palette lands in the slot it is dropped on', async ({ page, api }) => {
  site(api);
  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);

  const frame = page.frameLocator('iframe.gjs-frame');
  await expect(frame.locator('[data-dcms-type="dcms.heading"]')).toBeVisible({ timeout: 30000 });

  // Search, as an author would: the palette opens its first sections only.
  await page.getByPlaceholder('Search blocks').fill('Image');
  const block = page.getByText('Image', { exact: true }).first();
  const target = frame.locator('.dcms-node[data-dcms-node="hero"] .dcms-slot').first();
  await dragWithMouse(page, block, target);

  // GrapesJS's sorter, gated by canPlace, put it in the hero's slot; the save carries it.
  await expect(frame.locator('[data-dcms-node="hero"] [data-dcms-type="dcms.image"]')).toBeVisible();
  await expect.poll(() => savedHome(api), { timeout: 15000 }).toContain('"type": "dcms.image"');
});

test('a copy gets its own id at once, and delete removes exactly the selected component', async ({ page, api }) => {
  site(api);
  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);

  const frame = page.frameLocator('iframe.gjs-frame');
  const intro = frame.locator('[data-dcms-node="intro"]');
  await expect(intro).toBeVisible({ timeout: 30000 });

  await intro.locator('p').click();
  // GrapesJS's own toolbar: select parent, move, clone, delete.
  await page.locator('.gjs-toolbar').first().locator('.gjs-toolbar-item').nth(2).click();

  const texts = frame.locator('[data-dcms-type="dcms.text"]');
  await expect(texts).toHaveCount(2);
  const ids = await texts.evaluateAll((els) => els.map((e) => e.getAttribute('data-dcms-node')));
  expect(new Set(ids).size).toBe(2);

  await page.keyboard.press('Delete');
  await expect(texts).toHaveCount(1);
  await expect.poll(() => (savedHome(api)?.match(/"type": "dcms.text"/g) ?? []).length, { timeout: 15000 }).toBe(1);
});

test('switching pages swaps the canvas cleanly, with no errors and no edits written', async ({ page, api }) => {
  const doc = (id: string, text: string) =>
    `${JSON.stringify(
      { schemaVersion: 1, id, title: id, root: { id: `${id}-root`, type: 'dcms.page', slots: { default: [{ id: `${id}-h`, type: 'dcms.heading', props: { text } }] } } },
      null,
      2,
    )}\n`;
  const files = {
    'dcms/app.json': `${JSON.stringify({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }, { id: 'about', path: '/about', page: 'about' }] }, null, 2)}\n`,
    'dcms/pages/home.json': doc('home', 'Home page'),
    'dcms/pages/about.json': doc('about', 'About page'),
  };
  builderServer(api, {
    ...files,
    'src/api/manifest.json': API_MANIFEST,
    'src/dcms/runtime/index.ts': '// runtime\n',
    'src/dcms/runtime/manifest.json': RUNTIME_MANIFEST,
  });

  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => {
    if (m.type() === 'error' && !/WebSocket|SignalR|negotiat/i.test(m.text())) errors.push(m.text());
  });

  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);
  const frame = page.frameLocator('iframe.gjs-frame');
  const heading = frame.locator('[data-dcms-type="dcms.heading"]');
  await expect(heading).toHaveText('Home page', { timeout: 30000 });

  await page.getByRole('tab', { name: 'Pages' }).click();
  for (const [id, text] of [['about', 'About page'], ['home', 'Home page'], ['about', 'About page']] as const) {
    await page.getByRole('button', { name: new RegExp(`^${id}`) }).click();
    await expect(heading).toHaveText(text);
    // One page on the canvas at a time: the old page's views and React roots are gone.
    await expect(frame.locator('[data-dcms-type="dcms.page"]')).toHaveCount(1);
  }

  // Loading a page is not an edit: nothing was saved.
  await page.waitForTimeout(1500);
  expect(api.requestsTo('PATCH', IDE_FILES)).toHaveLength(0);
  expect(errors).toEqual([]);
});

const twoPages = {
  'dcms/app.json': doc({
    schemaVersion: 1,
    routes: [
      { id: 'home', path: '/', page: 'home' },
      { id: 'about', path: '/about', page: 'about' },
    ],
    navigation: { main: [{ label: 'Home', to: '/' }, { label: 'About', to: '/about' }] },
  }),
  'dcms/pages/home.json': doc({
    schemaVersion: 1,
    id: 'home',
    title: 'Home',
    root: {
      id: 'r',
      type: 'dcms.page',
      slots: {
        default: [
          { id: 'h', type: 'dcms.heading', props: { text: 'Welcome home', level: '1' } },
          { id: 'row', type: 'dcms.stack', props: { direction: 'horizontal' }, slots: { default: [
            { id: 'go', type: 'dcms.button', props: { label: 'Read about us' }, action: { type: 'navigate', to: '/about' } },
          ] } },
        ],
      },
    },
  }),
  'dcms/pages/about.json': doc({
    schemaVersion: 1,
    id: 'about',
    title: 'About',
    root: { id: 'r2', type: 'dcms.page', slots: { default: [{ id: 'h2', type: 'dcms.heading', props: { text: 'About us' } }] } },
  }),
};

test('the app shell draws the site’s menu and saves into app.json', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await page.getByRole('tab', { name: 'Pages' }).click();
  await page.getByRole('button', { name: /App shell/ }).click();

  // The default shell: the main menu, then where each page goes.
  await expect(frame.locator('.dcms-nav a')).toHaveText(['Home', 'About']);
  await expect(frame.getByText('Page content')).toBeVisible();

  await frame.locator('[data-dcms-type="dcms.section"]').first().click({ position: { x: 5, y: 5 } });
  await page.getByRole('combobox', { name: 'Vertical spacing' }).click();
  await page.getByRole('option', { name: 'lg', exact: true }).click();

  await expect.poll(() => saved(api, 'dcms/app.json') ?? '', { timeout: 15000 }).toContain('"shell"');
  const app = JSON.parse(saved(api, 'dcms/app.json')!);
  // The routes the shell edit wrote back are the ones that were there.
  expect(app.routes.map((r: { path: string }) => r.path)).toEqual(['/', '/about']);
  expect(JSON.stringify(app.shell)).toContain('"spacing":"lg"');
});

test('on Tablet an edit is an override for that size; desktop keeps its value', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="row"] .dcms-slot').first().click({ position: { x: 600, y: 5 } });
  await expect(page.getByText('Stack', { exact: true }).first()).toBeVisible();

  await page.getByTitle('Tablet').click();
  await page.getByRole('combobox', { name: 'Direction' }).click();
  await page.getByRole('option', { name: 'vertical', exact: true }).click();

  await expect.poll(() => saved(api, 'dcms/pages/home.json') ?? '', { timeout: 15000 }).toContain('"tablet"');
  const row = JSON.parse(saved(api, 'dcms/pages/home.json')!).root.slots.default[1];
  expect(row.props.direction).toBe('horizontal');
  expect(row.responsive).toEqual({ tablet: { direction: 'vertical' } });
  // At tablet width the canvas frame is the viewport, so the override is what it shows.
  await expect(frame.locator('[data-dcms-node="row"] .dcms-slot').first()).toHaveCSS('flex-direction', 'column');
});

test('adding a page writes its file, its route and a menu entry', async ({ page, api }) => {
  await open(page, api, twoPages);
  await page.getByRole('tab', { name: 'Pages' }).click();
  await page.getByRole('button', { name: 'Add page' }).click();
  await page.locator('#new-page-title').fill('Our Team');
  await page.getByRole('button', { name: 'Create' }).click();

  await expect.poll(() => saved(api, 'dcms/pages/our-team.json') ?? '', { timeout: 15000 }).toContain('"title": "Our Team"');
  const app = JSON.parse(saved(api, 'dcms/app.json')!);
  expect(app.routes).toContainEqual({ id: 'our-team', path: '/our-team', page: 'our-team' });
  expect(app.navigation.main).toContainEqual({ label: 'Our Team', to: '/our-team' });
});

test('preview runs the site: a button navigates to another page', async ({ page, api }) => {
  await open(page, api, twoPages);
  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();
  const preview = page.frameLocator('iframe[title="Preview"]');
  await expect(preview.getByRole('heading', { name: 'Welcome home' })).toBeVisible();
  await preview.getByRole('link', { name: 'Read about us' }).click();
  await expect(preview.getByRole('heading', { name: 'About us' })).toBeVisible();
});

test('publishing is refused while the site has errors, and says where they are', async ({ page, api }) => {
  const broken = {
    ...twoPages,
    'dcms/app.json': doc({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }, { id: 'x', path: '/x', page: 'missing' }] }),
  };
  await open(page, api, broken);
  await page.getByRole('button', { name: 'Publish' }).click();
  await expect(page.getByText('The route /x shows the page “missing”, which does not exist.')).toBeVisible();
  expect(api.requestsTo('POST', `/api/admin/sites/${SITE}/git/commit`)).toHaveLength(0);
});

test('a selection becomes a component; editing it starts v2; an exposed setting reaches the page after updating', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  page.on('dialog', (d) => void d.accept('Welcome title'));

  // Make the heading a component: v1 is written and the heading is replaced by an instance of it.
  await frame.locator('[data-dcms-node="h"] h1').click();
  await page.getByRole('tab', { name: 'My components' }).click();
  await page.getByRole('button', { name: 'Make the selection a component' }).click();

  await expect.poll(() => saved(api, 'dcms/components/welcome-title/v1.json') ?? '', { timeout: 15000 }).toContain('"label": "Welcome title"');
  await expect.poll(() => saved(api, 'dcms/pages/home.json') ?? '', { timeout: 15000 }).toContain('"type": "tenant.welcome-title"');
  const instance = JSON.parse(saved(api, 'dcms/pages/home.json')!).root.slots.default[0];
  expect(instance).toEqual({ id: 'h', type: 'tenant.welcome-title', version: 1 });
  // The instance draws the template: same heading, now coming from the component.
  await expect(frame.locator('[data-dcms-node="h"][data-dcms-type="tenant.welcome-title"] h1')).toHaveText('Welcome home');

  // The page uses v1, so editing starts v2.
  await page.getByRole('button', { name: 'Edit' }).click();
  await expect.poll(() => saved(api, 'dcms/components/welcome-title/v2.json') ?? '', { timeout: 15000 }).toContain('"version": 2');

  // In the composer: expose the heading's text as a setting of the component.
  await frame.locator('h1').first().click();
  await page.getByRole('button', { name: 'Expose “Text”' }).click();
  await expect.poll(() => saved(api, 'dcms/components/welcome-title/v2.json') ?? '', { timeout: 15000 }).toContain('"name": "text"');
  const v2 = JSON.parse(saved(api, 'dcms/components/welcome-title/v2.json')!);
  expect(v2.props[0]).toMatchObject({ kind: 'text', name: 'text', default: 'Welcome home' });

  // Back on the page, the instance is still v1 — until it is updated.
  await page.getByRole('tab', { name: 'Pages' }).click();
  await page.getByRole('button', { name: /^Home/ }).click();
  await frame.locator('[data-dcms-node="h"] h1').click();
  await page.getByRole('button', { name: 'Update to v2' }).click();
  const field = page.getByLabel('Heading: Text');
  await field.fill('Hello from a component');
  await field.press('Enter');

  await expect(frame.locator('[data-dcms-node="h"] h1')).toHaveText('Hello from a component');
  await expect.poll(() => saved(api, 'dcms/pages/home.json') ?? '', { timeout: 15000 }).toContain('"version": 2');
  expect(JSON.parse(saved(api, 'dcms/pages/home.json')!).root.slots.default[0]).toMatchObject({
    type: 'tenant.welcome-title',
    version: 2,
    props: { text: 'Hello from a component' },
  });
});

const POSTS = [
  { id: 'p1', slug: 'hello', contentType: 'post', data: { title: 'Hello world', body: '<p>First</p>' } },
  { id: 'p2', slug: 'second', contentType: 'post', data: { title: 'Second post', body: '<p>Two</p>' } },
];

const dataSite = {
  'dcms/app.json': doc({
    schemaVersion: 1,
    routes: [
      { id: 'home', path: '/', page: 'home' },
      { id: 'post', path: '/posts/:slug', page: 'post' },
    ],
  }),
  'dcms/pages/home.json': doc({
    schemaVersion: 1,
    id: 'home',
    title: 'Home',
    root: {
      id: 'r',
      type: 'dcms.page',
      slots: {
        default: [
          {
            id: 'list',
            type: 'dcms.collection',
            props: { source: { instance: 'press-room', contentType: 'post' }, limit: 6 },
            slots: {
              item: [
                {
                  id: 'card',
                  type: 'dcms.stack',
                  slots: {
                    default: [
                      { id: 'title', type: 'dcms.heading', props: { text: 'Heading' } },
                      { id: 'more', type: 'dcms.button', props: { label: 'Read' }, action: { type: 'navigate', to: '/posts/:slug' } },
                    ],
                  },
                },
              ],
            },
          },
        ],
      },
    },
  }),
  'dcms/pages/post.json': doc({
    schemaVersion: 1,
    id: 'post',
    title: 'Post',
    data: { source: { instance: 'press-room', contentType: 'post' }, param: 'slug' },
    root: { id: 'r2', type: 'dcms.page', slots: { default: [{ id: 'pt', type: 'dcms.heading', bind: { text: 'title' } }] } },
  }),
};

function content(api: MockApi) {
  api
    .on('GET', '/api/admin/sites/:id/preview/api/press-room/post', { items: POSTS, totalCount: 2, page: 1, pageSize: 6 })
    .on('GET', '/api/admin/sites/:id/preview/api/press-room/post/:slug', ({ params }) => POSTS.find((p) => p.slug === params.slug));
}

test('a collection shows real content on the canvas, and a heading in it binds to a field', async ({ page, api }) => {
  content(api);
  const frame = await open(page, api, dataSite);
  await expect(frame.getByText('This item is the first of 2; the site repeats it for each.')).toBeVisible();

  await frame.locator('[data-dcms-node="title"] h2').click();
  await page.getByRole('combobox', { name: 'What Text shows' }).click();
  await page.getByRole('option', { name: 'The item’s Title' }).click();

  // The canvas fills the bound heading from the first post…
  await expect(frame.locator('[data-dcms-node="title"] h2')).toHaveText('Hello world');
  // …and the page saves the binding, not the text.
  await expect.poll(() => saved(api, 'dcms/pages/home.json') ?? '', { timeout: 15000 }).toContain('"bind"');
  const card = JSON.parse(saved(api, 'dcms/pages/home.json')!).root.slots.default[0].slots.item[0];
  expect(card.slots.default[0].bind).toEqual({ text: 'title' });
});

test('preview lists every item and a card links to its detail page', async ({ page, api }) => {
  content(api);
  const bound = JSON.parse(dataSite['dcms/pages/home.json']);
  bound.root.slots.default[0].slots.item[0].slots.default[0].bind = { text: 'title' };
  await open(page, api, { ...dataSite, 'dcms/pages/home.json': doc(bound) });

  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();
  const preview = page.frameLocator('iframe[title="Preview"]');
  await expect(preview.getByRole('heading')).toHaveText(['Hello world', 'Second post']);
  await preview.getByRole('link', { name: 'Read' }).nth(1).click();
  await expect(preview.getByRole('heading', { name: 'Second post' })).toBeVisible();
  await expect(preview.getByRole('heading')).toHaveCount(1);
});
