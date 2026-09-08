import { test, expect, data } from '../fixtures/test';
import { dragOnto } from '../fixtures/dnd';

/**
 * The media library: the drag-and-drop the brief asked for, and the page tour.
 *
 * <p>Dragging is the one interaction in this product that a jsdom test genuinely cannot make a
 * claim about. `dnd.test.ts` covers what the payload should be; only a browser can say whether
 * dropping an asset on a folder tile actually moves it.</p>
 */

/*
 * A folder appears twice on this page by design — once in the rail and once as a tile in the
 * grid — so every folder locator here says which. The tile is the drop target.
 */
const tile = (page: import('@playwright/test').Page, name: string) =>
  page.getByRole('button', { name: new RegExp(`^${name} \\d+ file`) });

test('the library lists folders and assets together', async ({ page }) => {
  await page.goto('/media');

  await expect(tile(page, 'Brand')).toBeVisible();
  await expect(tile(page, 'Press kit')).toBeVisible();
  await expect(page.getByText('logo.png')).toBeVisible();
});

test('dragging an asset onto a folder moves it', async ({ page, api }) => {
  await page.goto('/media');
  await expect(page.getByText('unfiled-shot.png')).toBeVisible();

  const asset = page.locator('div.group').filter({ hasText: 'unfiled-shot.png' }).first();

  await dragOnto(asset, tile(page, 'Press kit'));

  await expect(page.getByText('Moved 1 file')).toBeVisible();

  const move = api.requestsTo('POST', '/api/admin/media/move').at(-1);
  expect(move?.body).toEqual({ ids: [data.ASSET_LOOSE], folderId: data.FOLDER_PRESS });
});

test('dragging one of several selected assets moves the whole selection', async ({ page, api }) => {
  await page.goto('/media');
  await expect(page.getByText('logo.png')).toBeVisible();

  // What every file manager does. Getting it backwards — moving only the dragged item while five
  // others sit selected — is the kind of surprise that costs somebody a re-sort of their library.
  const checkboxes = page.getByRole('button', { name: 'Select' });
  await expect(checkboxes).toHaveCount(data.MEDIA_ASSETS.length);
  for (let i = 0; i < data.MEDIA_ASSETS.length; i++) {
    // `force` because the checkbox is only opaque on hover; it is in the layout either way.
    await checkboxes.nth(i).click({ force: true });
  }

  /*
   * Wait for the selection bar before dragging, not merely for the clicks to return.
   * Selecting swaps the toolbar for the bulk-action bar, which reflows the grid — dragging into
   * that reflow resolved a stale element and the drop landed nowhere, about one run in three.
   */
  await expect(page.getByText(`${data.MEDIA_ASSETS.length} selected`)).toBeVisible();

  await dragOnto(page.locator('div.group').filter({ hasText: 'logo.png' }).first(), tile(page, 'Press kit'));

  await expect
    .poll(() => api.requestsTo('POST', '/api/admin/media/move').length)
    .toBeGreaterThan(0);
  const move = api.requestsTo('POST', '/api/admin/media/move').at(-1);
  expect((move?.body as { ids: string[] }).ids).toHaveLength(data.MEDIA_ASSETS.length);
});

test('an external drag is left to the uploader', async ({ page, api }) => {
  await page.goto('/media');
  await expect(tile(page, 'Press kit')).toBeVisible();

  // A drag with no `application/x-dcms-media` marker is a file from the desktop. The folder tile
  // has to stand aside rather than treat it as a move of nothing — that marker is the whole
  // reason the drop zone and the folder tiles can share a page.
  await tile(page, 'Press kit').dispatchEvent('drop', {
    dataTransfer: await page.evaluateHandle(() => new DataTransfer()),
  });

  await page.waitForTimeout(300);
  expect(api.requestsTo('POST', '/api/admin/media/move')).toEqual([]);
});

test('the page tour walks the library and can be left at any point', async ({ page }) => {
  await page.goto('/media');
  await expect(page.getByText('logo.png')).toBeVisible();

  await page.getByRole('button', { name: 'Show me around this page' }).click();

  const tour = page.getByRole('dialog', { name: 'Page tour' });
  await expect(tour).toBeVisible();
  await expect(tour.getByText('1 of 4')).toBeVisible();

  await tour.getByRole('button', { name: 'Next' }).click();
  await expect(tour.getByText('2 of 4')).toBeVisible();

  // Never forced: it is opt-in from a button and leaves on Escape.
  await page.keyboard.press('Escape');
  await expect(tour).toHaveCount(0);
});
