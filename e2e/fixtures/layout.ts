import type { Locator, Page } from '@playwright/test';

/**
 * The controls a phone cannot reach: off the screen, or cut off by a box that clips them.
 *
 * Returns their labels, so an assertion against `[]` names what broke. Scoped to `within` when
 * given — a dialog — and to the whole document otherwise, once running animations have settled. A control with no box (display:none,
 * a closed menu) is not counted: it is not there to reach.
 */
export async function unreachableControls(page: Page, within?: Locator): Promise<string[]> {
  const root = within ? await within.elementHandle() : null;
  return page.evaluate(async (scope) => {
    // A dialog measured mid-way through its scale-in reads as clipped. Wait out every finite
    // animation; an infinite one (a spinner) would never settle and is not moving the layout.
    await Promise.all(
      document
        .getAnimations()
        .filter((a) => a.effect?.getComputedTiming().endTime !== Infinity)
        .map((a) => a.finished.catch(() => undefined)),
    );
    const out: string[] = [];
    const selector = 'button, a[href], [role=tab], input, select, textarea';
    for (const el of (scope ?? document).querySelectorAll(selector)) {
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      let clipped = r.left < -1 || r.right > window.innerWidth + 1;
      for (let a = el.parentElement; a && !clipped; a = a.parentElement) {
        if (getComputedStyle(a).overflowX === 'visible') continue;
        const ar = a.getBoundingClientRect();
        clipped = r.left < ar.left - 1 || r.right > ar.right + 1;
      }
      if (clipped)
        out.push((el.getAttribute('aria-label') || (el as HTMLElement).innerText).trim());
    }
    return out;
  }, root);
}
