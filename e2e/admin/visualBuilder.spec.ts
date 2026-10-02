import type { Locator, Page } from '@playwright/test';
import type { MockApi } from '../fixtures/api';
import { test, expect } from '../fixtures/test';

/**
 * The Mode D builder: React components on a GrapesJS canvas (ADR 0020).
 *
 * <p>This is the go/no-go spike's gate in a real browser — the part jsdom cannot see. The
 * components must be drawn by React *inside* the canvas frame, clicking one must select it,
 * an inspector edit must reach the page file the draft saves, and undo must take it back.
 * What is asserted is the draft save — the JSON that actually leaves the browser.</p>
 */

const SITE = '33333333-3333-3333-3333-333333333333';
const IDE_FILES = `/api/admin/sites/${SITE}/ide/files`;
const HOME = 'dcms/pages/home.json';

function site(api: MockApi) {
  api
    .on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Acme', renderMode: 'ReactBuilder', slug: 'acme' })
    .on('GET', '/api/admin/sites/:id/ide', { branch: 'main', baseSha: 'abc1234', version: 1, files: {}, hashes: {} })
    .on('PATCH', '/api/admin/sites/:id/ide/files', { version: 2, hashes: {} });
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

  // The new site was seeded into the draft.
  await expect.poll(() => savedHome(api) !== null, { timeout: 15000 }).toBe(true);

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
  api
    .on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Acme', renderMode: 'ReactBuilder', slug: 'acme' })
    .on('GET', '/api/admin/sites/:id/ide', { branch: 'main', baseSha: 'abc1234', version: 1, files, hashes: {} })
    .on('PATCH', '/api/admin/sites/:id/ide/files', { version: 2, hashes: {} });

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

  for (const [id, text] of [['about', 'About page'], ['home', 'Home page'], ['about', 'About page']] as const) {
    await page.getByRole('combobox', { name: 'Page' }).click();
    await page.getByRole('option', { name: id, exact: true }).click();
    await expect(heading).toHaveText(text);
    // One page on the canvas at a time: the old page's views and React roots are gone.
    await expect(frame.locator('[data-dcms-type="dcms.page"]')).toHaveCount(1);
  }

  // Loading a page is not an edit: nothing was saved.
  await page.waitForTimeout(1500);
  expect(api.requestsTo('PATCH', IDE_FILES)).toHaveLength(0);
  expect(errors).toEqual([]);
});
