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

/**
 * Boxes whose content is wider than they are: a long token, hostname or URL running out of the
 * card it sits in. Still on the screen, often, so `unreachableControls` does not see it — the text
 * just crosses a border. Boxes that clip or scroll on purpose (`truncate`, `overflow-x-auto`) are
 * not counted.
 */
export async function spillingContent(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const out: string[] = [];
    for (const el of document.querySelectorAll<HTMLElement>('main *, [role=dialog] *')) {
      if (el.clientWidth === 0 || getComputedStyle(el).overflowX !== 'visible') continue;
      if (el.scrollWidth > el.clientWidth + 1) {
        out.push(`${el.tagName.toLowerCase()} "${el.innerText.trim().slice(0, 50)}"`);
      }
    }
    return out;
  });
}
