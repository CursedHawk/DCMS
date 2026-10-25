import { test, expect } from '../fixtures/test';
import type { Page } from '@playwright/test';

/**
 * Popups open where they end up. A popover's or dialog's top-left corner is sampled on every
 * frame from the click on: it may move by the few pixels the open animation's scale accounts
 * for, never jump. Dialog keyframes once doubled Tailwind's centring offset (and reached Radix
 * popovers through `role="dialog"`), and a centred dialog jumped as its data arrived.
 */
async function framesOf(page: Page, selector: string, open: () => Promise<void>) {
  await page.evaluate((sel) => {
    const w = window as unknown as { popupFrames: number[][] };
    w.popupFrames = [];
    const tick = () => {
      const el = document.querySelector(sel);
      if (el) {
        const r = el.getBoundingClientRect();
        w.popupFrames.push([r.x, r.y]);
      }
      if (w.popupFrames.length < 30) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }, selector);
  await open();
  await expect.poll(() => page.evaluate(() => (window as unknown as { popupFrames: number[][] }).popupFrames.length)).toBe(30);
  return page.evaluate(() => (window as unknown as { popupFrames: number[][] }).popupFrames);
}

function expectSteady(frames: number[][]) {
  const [x, y] = frames.at(-1)!;
  for (const [fx, fy] of frames) {
    expect(Math.abs(fx - x)).toBeLessThan(16);
    expect(Math.abs(fy - y)).toBeLessThan(16);
  }
}

test('a popover opens in place', async ({ page }) => {
  await page.goto('/');
  const bell = page.getByRole('button', { name: /notification/i }).first();
  await bell.waitFor();
  expectSteady(await framesOf(page, '[data-radix-popper-content-wrapper] > *', () => bell.click()));
});

test('a dialog opens in place, and stays there when its content arrives', async ({ page }) => {
  await page.goto('/media');
  const open = page.locator('div.group').filter({ hasText: 'logo.png' }).first().getByRole('button').nth(1);
  await open.waitFor();
  expectSteady(await framesOf(page, '[role=dialog].dcms-pop', () => open.click()));
});
