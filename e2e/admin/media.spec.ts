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

test('without a Google app configured there is no Drive button', async ({ page, api }) => {
  await page.goto('/media');
  await expect(page.getByText('Drop files here or click to upload')).toBeVisible();

  await expect(page.getByRole('button', { name: 'Import from Google Drive' })).toHaveCount(0);
  expect(api.requestsTo('GET', '/api/admin/media/google-drive/config')).not.toHaveLength(0);
});

test('a file picked in Google Drive is imported by the server, not uploaded', async ({ page, api }) => {
  api.on('GET', '/api/admin/media/google-drive/config', { clientId: 'client', apiKey: 'key', appId: '123' });
  api.on('POST', '/api/admin/media/google-drive/import', { id: data.ASSET_LOOSE });

  // Google's scripts, faked: sign-in grants a token at once, and the picker "picks" one file
  // as soon as it is shown. What is under test is everything after Google hands over.
  for (const src of ['https://accounts.google.com/gsi/client', 'https://apis.google.com/js/api.js']) {
    await page.route(src, (route) => route.fulfill({ contentType: 'text/javascript', body: '' }));
  }
  await page.addInitScript(() => {
    const self = function (this: unknown) {
      return this;
    };
    const w = window as unknown as Record<string, unknown>;
    w.gapi = { load: (_: string, cb: () => void) => cb() };
    w.google = {
      accounts: {
        oauth2: {
          initTokenClient: (c: { callback: (r: object) => void }) => ({
            requestAccessToken: () => c.callback({ access_token: 'drive-token', expires_in: 3600 }),
          }),
        },
      },
      picker: {
        ViewId: { DOCS: 'all' },
        Feature: { MULTISELECT_ENABLED: 'multi', SUPPORT_DRIVES: 'drives' },
        Action: { PICKED: 'picked', CANCEL: 'cancel' },
        DocsView: class {
          setMimeTypes = self;
          setIncludeFolders = self;
          setSelectFolderEnabled = self;
          setEnableDrives = self;
          setOwnedByMe = self;
        },
        PickerBuilder: class {
          private cb: (r: object) => void = () => undefined;
          setAppId = self;
          setDeveloperKey = self;
          setOAuthToken = self;
          setOrigin = self;
          setLocale = self;
          setMaxItems = self;
          addView = self;
          enableFeature = self;
          setCallback(cb: (r: object) => void) {
            this.cb = cb;
            return this;
          }
          build() {
            const cb = this.cb;
            return {
              setVisible: () =>
                setTimeout(() => cb({ action: 'picked', docs: [{ id: 'drive-file-1', name: 'beach.jpg', sizeBytes: 2048 }] })),
              dispose: () => undefined,
            };
          }
        },
      },
    };
  });

  await page.goto('/media');
  await page.getByRole('button', { name: 'Import from Google Drive' }).click();

  await expect
    .poll(() => api.requestsTo('POST', '/api/admin/media/google-drive/import').length)
    .toBe(1);
  expect(api.requestsTo('POST', '/api/admin/media/google-drive/import')[0].body).toEqual({
    accessToken: 'drive-token',
    fileId: 'drive-file-1',
    folderId: null,
  });
  // The bytes never pass through the browser: no multipart upload was made.
  expect(api.requestsTo('POST', '/api/admin/media')).toEqual([]);
});

test('a full library refuses an upload without sending it', async ({ page, api }) => {
  api.on('GET', '/api/admin/media/usage', { ...data.MEDIA_USAGE, quotaBytes: data.MEDIA_USAGE.totalBytes });

  await page.goto('/media');
  await expect(page.getByText('Storage is full.', { exact: false })).toBeVisible();

  await page.locator('input[type=file]').first().setInputFiles({
    name: 'more.png',
    mimeType: 'image/png',
    buffer: Buffer.from('not really a png'),
  });

  await expect(page.getByText('Not enough storage left', { exact: false })).toBeVisible();
  expect(api.requestsTo('POST', '/api/admin/media')).toHaveLength(0);
});
