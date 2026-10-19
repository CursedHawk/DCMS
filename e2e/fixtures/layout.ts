import type { Locator, Page } from '@playwright/test';
import type { MockApi } from './api';
import * as data from './data';

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
 * not counted. Nor is a page's own top bar (`header`): its count badges overhang their buttons
 * on purpose, exactly as the app topbar's do — which sits outside `main` and was never measured.
 */
export async function spillingContent(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const out: string[] = [];
    for (const el of document.querySelectorAll<HTMLElement>('main *:not(header *), [role=dialog] *')) {
      if (el.clientWidth === 0 || getComputedStyle(el).overflowX !== 'visible') continue;
      if (el.scrollWidth > el.clientWidth + 1) {
        out.push(`${el.tagName.toLowerCase()} "${el.innerText.trim().slice(0, 50)}"`);
      }
    }
    return out;
  });
}

/**
 * How far `main` scrolls sideways. `AppFrame` clips the document, so the document itself never
 * overflows — a page that is too wide scrolls inside `main` instead, and only this sees it.
 */
export async function mainOverflow(page: Page): Promise<number> {
  return page
    .locator('main')
    .first()
    .evaluate((m) => m.scrollWidth - m.clientWidth);
}

/*
 * Names as long as people actually type them. The shared fixtures say "Acme Studio" and
 * "Press room", which fit anywhere, so a sweep over them could not tell a layout that wraps from
 * one that does not. One unbroken token each, without hyphens (a browser breaks a line at a hyphen), because that is the case `truncate`-less flex rows
 * and grids get wrong: a sentence wraps on its own, a slug or an email address does not.
 */
const LONG = 'QuarterlyInvestorRelationsAndPressAnnouncementsArchive2026Edition';
const LONG_EMAIL = 'maximilian.alexander.featherstonehaugh.wolfeschlegelsteinhausen@example.test';

/** Re-answers the list endpoints the sweep visits with long names. Later `api.on` wins. */
export function useLongStrings(api: MockApi): void {
  api
    .on('GET', '/api/admin/me/tenants', [
      { ...data.TENANT, name: `${LONG} Studio` },
      data.OTHER_TENANT,
    ])
    .on('GET', '/api/admin/sites', [
      { id: '11111111-1111-1111-1111-111111111111', name: LONG, renderMode: 'StaticPrerender' },
    ])
    .on('GET', '/api/admin/plugins/instances', [
      { ...data.PLUGIN_INSTANCES[0], name: LONG, description: `${LONG} ${LONG}` },
    ])
    .on('GET', '/api/admin/content/page', {
      items: data.CONTENT_ROWS.map((r) => ({
        ...r,
        title: `${r.title} ${LONG}`,
        slug: LONG.toLowerCase(),
      })),
      nextCursor: null,
      total: data.CONTENT_ROWS.length,
    })
    .on('GET', '/api/admin/members', [
      {
        membershipId: '88888888-0000-0000-0000-000000000009',
        userId: 'u9',
        email: LONG_EMAIL,
        roleIds: [],
      },
    ])
    .on('GET', '/api/admin/tenant', {
      ...data.WORKSPACE,
      owners: [{ userId: '11111111-1111-1111-1111-111111111111', email: LONG_EMAIL }],
    })
    .on(
      'GET',
      '/api/admin/media',
      data.MEDIA_ASSETS.map((a) => ({ ...a, fileName: `${LONG}.png` })),
    )
    .on('GET', '/account/api/me', {
      email: LONG_EMAIL,
      displayName: LONG,
      forgejoUsername: LONG.toLowerCase(),
      hasGitPassword: true,
    });
}
