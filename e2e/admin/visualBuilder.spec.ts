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
async function dragWithMouse(page: Page, from: Locator, to: Locator, whileOver?: () => Promise<void>) {
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
  await whileOver?.();
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

  // Search, as an author would — by what they want ("photo"), not by the component's name.
  await page.getByPlaceholder('Search components').fill('photo');
  const block = page.getByText('Image', { exact: true }).first();
  const target = frame.locator('.dcms-node[data-dcms-node="hero"] .dcms-slot').first();
  // While over the hero, the builder says where it would land.
  await dragWithMouse(page, block, target, () => expect(page.getByRole('status').filter({ hasText: 'Drop into Section' })).toBeVisible());
  await expect(page.getByRole('status').filter({ hasText: 'Drop into' })).toHaveCount(0);

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
  // A short choice is shown whole: one click, no dropdown.
  await page.getByRole('radiogroup', { name: 'Vertical spacing' }).getByRole('radio', { name: 'Large' }).click();

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

  await page.getByTitle('Tablet', { exact: true }).click();
  await page.getByRole('combobox', { name: 'Direction' }).click();
  await page.getByRole('option', { name: 'One under another', exact: true }).click();

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

test('a page started from a template arrives with its sections and a suggested title and address', async ({ page, api }) => {
  await open(page, api, twoPages);
  await page.getByRole('tab', { name: 'Pages' }).click();
  await page.getByRole('button', { name: 'Add page' }).click();
  await page.getByRole('radio', { name: /^Services/ }).click();
  await expect(page.locator('#new-page-title')).toHaveValue('Services');
  await page.getByRole('button', { name: 'Create' }).click();

  await expect.poll(() => saved(api, 'dcms/pages/services.json') ?? '', { timeout: 15000 }).toContain('What we do');
  const services = JSON.parse(saved(api, 'dcms/pages/services.json')!);
  expect(services.root.slots.default).toHaveLength(5);
  expect(JSON.parse(saved(api, 'dcms/app.json')!).routes).toContainEqual({ id: 'services', path: '/services', page: 'services' });
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

test('a selection becomes a component in the studio; editing it starts v2; a setting reaches the page after updating', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  page.on('dialog', (d) => void d.accept('Welcome title'));

  // Make the heading a component: v1 is written, the heading replaced by an instance, and the
  // studio opens on the component.
  await frame.locator('[data-dcms-node="h"] h1').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Make reusable…' }).click();
  const studio = page.getByRole('region', { name: 'Component studio' });
  await expect(studio).toBeVisible();
  await expect(studio.getByLabel('Component name')).toHaveValue('Welcome title');
  await expect.poll(() => saved(api, 'dcms/pages/home.json') ?? '', { timeout: 15000 }).toContain('"type": "tenant.welcome-title"');
  expect(JSON.parse(saved(api, 'dcms/pages/home.json')!).root.slots.default[0]).toEqual({ id: 'h', type: 'tenant.welcome-title', version: 1 });

  // Done: back on the page with the instance selected — it draws the template.
  await studio.getByRole('button', { name: 'Done' }).click();
  await expect(studio).toHaveCount(0);
  await expect(frame.locator('[data-dcms-node="h"][data-dcms-type="tenant.welcome-title"] h1')).toHaveText('Welcome home');
  await expect(page.getByRole('navigation', { name: 'Selected part and the parts around it' }).getByRole('button').last()).toHaveText('Welcome title');

  // The page uses v1, so editing starts v2.
  await page.getByRole('tab', { name: 'My components' }).click();
  await page.getByRole('button', { name: 'Edit' }).click();
  await expect.poll(() => saved(api, 'dcms/components/welcome-title/v2.json') ?? '', { timeout: 15000 }).toContain('"version": 2');

  // In the studio's Settings tab: offer the heading's text to pages, under a friendlier label.
  await page.getByRole('tab', { name: 'Settings' }).click();
  await page.getByRole('button', { name: 'Text', exact: true }).click();
  await expect.poll(() => saved(api, 'dcms/components/welcome-title/v2.json') ?? '', { timeout: 15000 }).toContain('"name": "text"');
  const label = page.getByRole('group', { name: 'Heading: Text' }).getByLabel('Label');
  await label.fill('Title');
  await label.press('Enter');
  await expect.poll(() => saved(api, 'dcms/components/welcome-title/v2.json') ?? '', { timeout: 15000 }).toContain('"label": "Title"');
  expect(JSON.parse(saved(api, 'dcms/components/welcome-title/v2.json')!).props[0]).toMatchObject({ kind: 'text', name: 'text', default: 'Welcome home' });

  // Back on the page, the instance is still v1 — until it is updated.
  await page.getByRole('button', { name: 'Done' }).click();
  await page.getByRole('button', { name: 'Update to v2' }).click();
  const field = page.getByLabel('Title', { exact: true });
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

test('the studio turns an empty area into a slot, previews an instance and lists versions', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  page.on('dialog', (d) => void d.accept('Promo'));
  await frame.locator('[data-dcms-node="go"]').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Wrap in a Card' }).click();
  // The card has empty media and footer areas; make it a component.
  // Its corner, not its middle: the middle is the button inside.
  await frame.locator('[data-dcms-type="dcms.card"]').first().click({ button: 'right', position: { x: 3, y: 3 } });
  await page.getByRole('menuitem', { name: 'Make reusable…' }).click();
  await expect(page.getByRole('region', { name: 'Component studio' })).toBeVisible();

  await page.getByRole('tab', { name: 'Slots' }).click();
  await page.getByText('Card › Bottom').locator('..').getByRole('button', { name: 'Let pages fill' }).click();
  await expect.poll(() => saved(api, 'dcms/components/promo/v1.json') ?? '', { timeout: 15000 }).toContain('"slotTargets"');
  expect(JSON.parse(saved(api, 'dcms/components/promo/v1.json')!).slots).toEqual([{ name: 'footer', label: 'Bottom' }]);
  // A filled area cannot become a slot, and says why.
  await expect(page.getByText('Card › Content').locator('..')).toContainText('Has parts');

  await page.getByRole('tab', { name: 'Preview' }).click();
  await page.getByRole('button', { name: 'Show the preview' }).click();
  const preview = page.getByRole('dialog', { name: 'Preview of “Promo”' });
  const mobile = preview.getByTitle('Mobile');
  await mobile.click();
  await expect(mobile).toHaveAttribute('aria-pressed', 'true');
  await page.keyboard.press('Escape');

  await page.getByRole('tab', { name: 'Versions' }).click();
  await expect(page.getByRole('list', { name: 'Versions' }).getByRole('listitem')).toHaveCount(1);
  await expect(page.getByRole('button', { name: /^Start version 2/ })).toBeVisible();
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

test('an instance’s own CSS applies on the canvas and saves with the node; unsafe CSS is refused', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="h"] h1').click();
  await page.getByText('Advanced', { exact: true }).click();
  const css = page.getByLabel('Custom CSS for this instance');

  await css.fill('color: red } body { display: none');
  await css.blur();
  await expect(page.getByText(/may hold declarations only/)).toBeVisible();

  await css.fill('letter-spacing: 12px');
  await css.blur();
  await expect(frame.locator('[data-dcms-node="h"] h1')).toHaveCSS('letter-spacing', '12px');
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"css": "letter-spacing: 12px"');
});

test('page state: declared in Pages, set by a button, shown when — on the site, while the canvas shows all', async ({ page, api }) => {
  const home = JSON.parse(twoPages['dcms/pages/home.json']);
  home.state = { open: false };
  home.root.slots.default.push(
    { id: 'more', type: 'dcms.button', props: { label: 'More' }, action: { type: 'toggle-state', key: 'open' } },
    { id: 'details', type: 'dcms.text', props: { text: 'The details' }, when: { state: 'open' } },
  );
  const frame = await open(page, api, { ...twoPages, 'dcms/pages/home.json': doc(home) });

  // The canvas edits everything, hidden or not; the inspector says what hides it.
  await expect(frame.getByText('The details')).toBeVisible();
  await frame.locator('[data-dcms-node="details"] p').click();
  await expect(page.getByRole('combobox', { name: 'Show when' })).toHaveText('open');

  // A new state, declared in the page's settings.
  await page.getByRole('tab', { name: 'Pages' }).click();
  await page.getByRole('button', { name: 'Show settings' }).first().click();
  await page.getByLabel('New state name').fill('tab');
  await page.getByRole('button', { name: 'Add state' }).click();
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"tab": false');

  // On the site the details wait for the button.
  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();
  const preview = page.frameLocator('iframe[title="Preview"]');
  await expect(preview.getByRole('button', { name: 'More' })).toBeVisible();
  await expect(preview.getByText('The details')).toHaveCount(0);
  await preview.getByRole('button', { name: 'More' }).click();
  await expect(preview.getByText('The details')).toBeVisible();
  await preview.getByRole('button', { name: 'More' }).click();
  await expect(preview.getByText('The details')).toHaveCount(0);
});

test('the inspector groups settings and shows short choices whole: swatches, segments, icons', async ({ page, api }) => {
  const frame = await open(page, api, {
    ...twoPages,
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
              id: 'band',
              type: 'dcms.section',
              slots: { default: [{ id: 'h', type: 'dcms.heading', props: { text: 'Hello', level: '1' } }, { id: 'row', type: 'dcms.stack', props: { direction: 'horizontal' } }] },
            },
          ],
        },
      },
    }),
  });

  // Heading: alignment as icon buttons.
  await frame.locator('[data-dcms-node="h"] h1').click();
  await expect(page.getByRole('button', { name: 'Content', exact: true })).toHaveAttribute('aria-expanded', 'true');
  await page.getByRole('radiogroup', { name: 'Alignment' }).getByRole('radio', { name: 'Centre' }).click();
  await expect(frame.locator('[data-dcms-node="h"] h1')).toHaveClass(/dcms-text-center/);

  // A section's background is chosen by its colour.
  await frame.locator('[data-dcms-node="band"]').click({ position: { x: 5, y: 5 } });
  await page.getByRole('radiogroup', { name: 'Background' }).getByRole('radio', { name: 'Dark' }).click();
  await expect(frame.locator('[data-dcms-node="band"] section')).toHaveClass(/dcms-bg-inverse/);

  // Help is one hover away instead of a paragraph under every field.
  await frame.locator('[data-dcms-node="row"]').click();
  await expect(page.getByRole('combobox', { name: 'Space between' })).toBeVisible();
  await expect(page.getByText('The gap between neighbouring items.')).toHaveCount(0);
  await page.getByRole('button', { name: 'What “Space between” does' }).hover();
  await expect(page.getByRole('tooltip')).toContainText('The gap between neighbouring items.');

  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"background": "inverse"');
  expect(saved(api, HOME)).toContain('"align": "center"');
});

test('an empty container says what it takes', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="h"] h1').click();
  await page.getByRole('button', { name: 'Stack', exact: true }).click();
  await expect(frame.locator('[data-dcms-type="dcms.stack"]').last().locator('.dcms-slot').first()).toHaveAttribute('data-hint', /^Drop parts here/);
  await page.getByPlaceholder('Search components').fill('gallery');
  await page.getByRole('button', { name: 'Gallery', exact: true }).click();
  await expect(frame.locator('[data-dcms-type="dcms.gallery"] .dcms-slot').first()).toHaveAttribute('data-hint', 'Takes Image — drag one here');
});

test('clicking a palette card adds it after the selection and selects it', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="h"] h1').click();
  await page.getByRole('button', { name: 'Text', exact: true }).click();
  // Right after the heading, selected, with its default copy.
  await expect(frame.locator('[data-dcms-type="dcms.text"]')).toHaveText('Write something here.');
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"type": "dcms.text"');
  const home = JSON.parse(saved(api, HOME)!);
  expect(home.root.slots.default.map((n: { type: string }) => n.type)).toEqual(['dcms.heading', 'dcms.text', 'dcms.stack']);
  await expect(page.getByRole('combobox', { name: 'Tone' }).or(page.getByRole('radiogroup', { name: 'Tone' }))).toBeVisible();
});

test('a section template from the Sections tab lands below the selected band, whole and editable', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="h"] h1').click();
  // The selection toolbar's + opens the Sections tab.
  await page.getByTitle('Add a section below').click();
  await expect(page.getByRole('tab', { name: 'Sections' })).toHaveAttribute('aria-selected', 'true');
  await page.getByPlaceholder('Search components').fill('prices');
  await page.getByRole('button', { name: /^Pricing/ }).click();
  await expect(frame.getByText('Simple, honest prices')).toBeVisible();
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('Simple, honest prices');
  const home = JSON.parse(saved(api, HOME)!);
  expect(home.root.slots.default.map((n: { type: string }) => n.type)).toEqual(['dcms.heading', 'dcms.section', 'dcms.stack']);
  // Ordinary parts: a heading inside it selects and edits like any other.
  await frame.getByText('Simple, honest prices').click();
  await expect(page.getByLabel('Text', { exact: true })).toHaveValue('Simple, honest prices');
});

test('right-click offers what can be done to a part; wrapping keeps it, inside the new container', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="go"]').click({ button: 'right' });
  const menu = page.getByRole('menu', { name: 'Actions for Button' });
  await expect(menu).toBeVisible();
  await menu.getByRole('menuitem', { name: 'Wrap in a Card' }).click();
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"type": "dcms.card"');
  const row = JSON.parse(saved(api, HOME)!).root.slots.default[1];
  expect(row.slots.default[0].type).toBe('dcms.card');
  expect(row.slots.default[0].slots.default.map((n: { id: string }) => n.id)).toEqual(['go']);
  // The breadcrumb shows where the new card sits, and walks up from it.
  const trail = page.getByRole('navigation', { name: 'Selected part and the parts around it' });
  await expect(trail.getByRole('button')).toHaveText(['Page', 'Stack', 'Card']);
  await trail.getByRole('button', { name: 'Stack' }).click();
  await expect(trail.getByRole('button')).toHaveText(['Page', 'Stack']);
});

test('the keyboard moves, duplicates and walks up; ? lists the keys', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="h"] h1').click();
  await page.keyboard.press('Alt+ArrowDown');
  await expect.poll(() => JSON.parse(saved(api, HOME) ?? '{"root":{"slots":{"default":[]}}}').root.slots.default.map((n: { id: string }) => n.id), { timeout: 15000 }).toEqual(['row', 'h']);
  await page.keyboard.press('Control+d');
  await expect.poll(() => JSON.parse(saved(api, HOME)!).root.slots.default.length, { timeout: 15000 }).toBe(3);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('navigation', { name: 'Selected part and the parts around it' }).getByRole('button')).toHaveText(['Page']);
  await page.keyboard.press('Shift+Slash');
  await expect(page.getByRole('dialog', { name: 'Keyboard shortcuts' })).toContainText('Move earlier or later');
});

test('a double-click types straight into a heading; Escape drops it, Enter keeps it, and it undoes', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  const heading = frame.locator('[data-dcms-node="h"] h1');
  await heading.dblclick();
  await page.keyboard.type('Never mind');
  await page.keyboard.press('Escape');
  await expect(heading).toHaveText('Welcome home');

  await heading.dblclick();
  await page.keyboard.type('Hello there');
  await page.keyboard.press('Enter');
  await expect(heading).toHaveText('Hello there');
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"text": "Hello there"');
  // The inspector shows the new text, and the button's label edits the same way.
  await expect(page.getByLabel('Text', { exact: true })).toHaveValue('Hello there');
  await page.keyboard.press('Control+z');
  await expect(heading).toHaveText('Welcome home');

  await frame.locator('[data-dcms-node="go"]').getByText('Read about us').dblclick();
  await page.keyboard.type('Meet us');
  await frame.locator('[data-dcms-node="h"]').click();
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"label": "Meet us"');
});

test('a design kit is adjusted — corners, colour — live on the canvas, and resets exactly', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await page.getByRole('tab', { name: 'Theme' }).click();
  await page.getByRole('button', { name: /^Studio/ }).click();
  await page.getByRole('radio', { name: 'Square' }).click();
  await expect.poll(() => saved(api, 'dcms/theme.json') ?? '', { timeout: 15000 }).toContain('"roundness": 0');
  expect(JSON.parse(saved(api, 'dcms/theme.json')!)).toMatchObject({ kit: 'studio', radius: '0rem' });
  await expect(frame.locator('[data-dcms-node="go"] .dcms-button')).toHaveCSS('border-radius', '0px');

  await page.getByLabel('Brand colour').fill('#fde047');
  await expect.poll(() => JSON.parse(saved(api, 'dcms/theme.json') ?? '{}').colors?.['brand-contrast'], { timeout: 15000 }).toBe('#111827');
  await expect(page.getByText(/Hard to read as text on the page background/)).toBeVisible();

  await page.getByRole('button', { name: 'Reset to kit' }).click();
  await expect.poll(() => saved(api, 'dcms/theme.json') ?? '', { timeout: 15000 }).not.toContain('tuning');
  expect(JSON.parse(saved(api, 'dcms/theme.json')!).radius).toBe('0.625rem');
});

test('an icon is added from the palette and its symbol picked by sight', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="h"] h1').click();
  await page.getByPlaceholder('Search components').fill('symbol');
  await page.getByRole('button', { name: 'Icon', exact: true }).click();
  await page.getByRole('button', { name: /^Icon: star/ }).click();
  await page.getByLabel('Search icons').fill('phone');
  await page.getByRole('option', { name: 'phone', exact: true }).click();
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"icon": "phone"');
  await expect(frame.locator('[data-dcms-type="dcms.icon"] svg')).toBeVisible();
});

test('a video takes a pasted YouTube link and shows a still on the canvas; settings that do not apply hide', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="h"] h1').click();
  await page.getByPlaceholder('Search components').fill('youtube');
  await page.getByRole('button', { name: 'Video', exact: true }).click();
  // A link source: the link field, no file picker.
  await expect(page.getByLabel('Video file', { exact: true })).toHaveCount(0);
  const link = page.getByLabel('Video link', { exact: true });
  await link.fill('https://youtu.be/dQw4w9WgXcQ');
  await link.press('Enter');
  await expect(frame.locator('.dcms-video-still')).toBeVisible();
  await expect(frame.locator('[data-dcms-type="dcms.video"] iframe')).toHaveCount(0);
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"url": "https://youtu.be/dQw4w9WgXcQ"');
});

test('an accordion arrives with questions open on the canvas; on the site they open on click, and tabs switch', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  await frame.locator('[data-dcms-node="h"] h1').click();
  await page.getByPlaceholder('Search components').fill('faq');
  await page.getByRole('button', { name: 'Accordion', exact: true }).click();
  await expect(frame.getByText('How long does delivery take?')).toBeVisible();
  // Every answer is open to be edited.
  await expect(frame.getByText('Usually two to three working days.')).toBeVisible();

  await page.getByPlaceholder('Search components').fill('tabs');
  await page.getByRole('button', { name: 'Tabs', exact: true }).click();
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"type": "dcms.tab"');

  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();
  const preview = page.frameLocator('iframe[title="Preview"]');
  await expect(preview.getByText('Usually two to three working days.')).toBeHidden();
  await preview.getByText('How long does delivery take?').click();
  await expect(preview.getByText('Usually two to three working days.')).toBeVisible();
  await expect(preview.getByText('What it is, in a sentence or two.')).toBeVisible();
  await preview.getByRole('tab', { name: 'Prices' }).click();
  await expect(preview.getByText('What it costs.')).toBeVisible();
  await expect(preview.getByText('What it is, in a sentence or two.')).toBeHidden();
});

test('on a phone the menu folds behind a button that opens it; the current page is marked', async ({ page, api }) => {
  const app = JSON.parse(twoPages['dcms/app.json']);
  app.shell = { id: 'sh', type: 'dcms.page', slots: { default: [{ id: 'nav', type: 'dcms.nav' }, { id: 'out', type: 'dcms.outlet' }] } };
  await open(page, api, { ...twoPages, 'dcms/app.json': doc(app) });
  await page.getByTitle('Mobile', { exact: true }).click();
  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();
  const preview = page.frameLocator('iframe[title="Preview"]');
  const menu = preview.getByRole('button', { name: 'Menu' });
  await expect(menu).toHaveAttribute('aria-expanded', 'false');
  await expect(preview.getByRole('link', { name: 'About', exact: true })).toBeHidden();
  await menu.click();
  await preview.getByRole('link', { name: 'About', exact: true }).click();
  await expect(preview.getByRole('heading', { name: 'About us' })).toBeVisible();
  // Following a link closes the menu; reopened, it shows where the visitor is.
  await expect(menu).toHaveAttribute('aria-expanded', 'false');
  await menu.click();
  await expect(preview.getByRole('link', { name: 'About', exact: true })).toHaveAttribute('aria-current', 'page');
});

test.describe('on a first visit', () => {
  test.use({ toursSeen: false });

  test('the builder tour starts by itself, and only then', async ({ page, api }) => {
    await open(page, api, twoPages);
    const tour = page.getByRole('dialog', { name: 'Page tour' });
    await expect(tour.getByRole('heading', { name: 'Parts and sections' })).toBeVisible();
    await tour.getByRole('button', { name: 'Next' }).click();
    await expect(tour.getByRole('heading', { name: 'Your page' })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(tour).toHaveCount(0);

    await page.reload();
    await expect(page.frameLocator('iframe.gjs-frame').locator('[data-dcms-type="dcms.heading"]').first()).toBeVisible({ timeout: 30000 });
    await page.waitForTimeout(500);
    await expect(tour).toHaveCount(0);
    // Help shows it again.
    await page.getByRole('button', { name: 'Help', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Show me around the builder' }).click();
    await expect(tour.getByRole('heading', { name: 'Parts and sections' })).toBeVisible();
  });
});

test('a task tour waits for what its step asks, and carries on into the studio', async ({ page, api }) => {
  const frame = await open(page, api, twoPages);
  const tour = page.getByRole('dialog', { name: 'Page tour' });
  await page.getByRole('button', { name: 'Help', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Build your first page' }).click();
  await expect(tour.getByRole('heading', { name: 'Add a section' })).toBeVisible();
  await page.getByRole('tab', { name: 'Sections' }).click();
  await page.getByPlaceholder('Search components').fill('prices');
  await page.getByRole('button', { name: /^Pricing/ }).click();
  await expect(tour.getByRole('heading', { name: 'Change the words' })).toBeVisible();
  await page.keyboard.press('Escape');

  page.on('dialog', (d) => void d.accept('Welcome title'));
  await page.getByRole('button', { name: 'Help', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Make a reusable component' }).click();
  await expect(tour.getByRole('heading', { name: 'Your components' })).toBeVisible();
  await tour.getByRole('button', { name: 'Next' }).click();
  await expect(tour.getByRole('heading', { name: 'Make one from a section' })).toBeVisible();
  await frame.locator('[data-dcms-node="h"] h1').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Make reusable…' }).click();
  await expect(page.getByRole('region', { name: 'Component studio' })).toBeVisible();
  await expect(tour.getByRole('heading', { name: 'What pages may change' })).toBeVisible();
});
