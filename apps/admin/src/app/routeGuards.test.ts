import { describe, expect, it } from 'vitest';
import { NAV } from './nav';
import { LEGACY_SETTINGS_PATHS, OPEN_ROUTES, ROUTE_GUARDS, SETTINGS_SECTIONS } from './routeGuards';

/**
 * The sidebar and the router have to agree.
 *
 * They are two lists of the same fact — what a page requires — and nothing but this file stops
 * them drifting. Drift in one direction gives a nav entry that leads straight to a refusal; in
 * the other, a page with no link, which anyone entitled to it can then never find.
 */
describe('nav and route guards agree', () => {
  it.each(NAV.map((i) => [i.to, i] as const))('%s', (to, item) => {
    const guard = ROUTE_GUARDS[to];

    if (item.superAdmin) {
      expect(guard, `${to} is SuperAdmin-only in the nav but unguarded in the router`).toEqual({
        superAdmin: true,
      });
      return;
    }

    if (item.perm) {
      expect(guard?.perm, `${to} requires ${item.perm} in the nav`).toBe(item.perm);
      return;
    }

    expect(
      OPEN_ROUTES.includes(to),
      `${to} names no permission in the nav, so it must be listed in OPEN_ROUTES`,
    ).toBe(true);
  });

  it('guards no path that is also declared open', () => {
    const both = Object.keys(ROUTE_GUARDS).filter((p) => OPEN_ROUTES.includes(p));
    expect(both, 'a path cannot be both guarded and open').toEqual([]);
  });

  it('guards every nav destination that names a permission', () => {
    const unguarded = NAV.filter((i) => (i.perm || i.superAdmin) && !ROUTE_GUARDS[i.to]);
    expect(unguarded.map((i) => i.to)).toEqual([]);
  });

  it('covers the site workspace, which has no nav entry of its own', () => {
    // Reached from /sites rather than the sidebar, and the heaviest page in the app to render
    // for somebody who may not open it.
    expect(ROUTE_GUARDS['/sites/$siteId']).toEqual(ROUTE_GUARDS['/sites']);
  });
});

/**
 * Settings is one nav entry over seven pages, so the three lists that used to be two now have to
 * agree three ways: the sub-navigation, the router's guards, and where the old URLs point.
 */
describe('settings sections', () => {
  it.each(SETTINGS_SECTIONS.map((s) => [s.to, s] as const))('%s', (to, section) => {
    if (section.perm) {
      expect(ROUTE_GUARDS[to]?.perm, `${to} is filtered on ${section.perm} in the sub-nav`).toBe(
        section.perm,
      );
      return;
    }

    expect(
      OPEN_ROUTES.includes(to),
      `${to} names no permission, so it must be listed in OPEN_ROUTES`,
    ).toBe(true);
  });

  /*
   * The landing page forwards to the first section the caller may open. If every section were
   * gated, somebody holding none of those permissions would be forwarded nowhere and would sit
   * on a spinner — so at least one has to be open to any member.
   */
  it('always has somewhere to forward to', () => {
    expect(SETTINGS_SECTIONS.some((s) => !s.perm)).toBe(true);
  });

  it('lives entirely under /settings', () => {
    expect(SETTINGS_SECTIONS.filter((s) => !s.to.startsWith('/settings/'))).toEqual([]);
  });

  it('has no duplicate section', () => {
    const paths = SETTINGS_SECTIONS.map((s) => s.to);
    expect(new Set(paths).size).toBe(paths.length);
  });
});

describe('the old top-level URLs', () => {
  /*
   * These are load-bearing. Notification rows carry `linkPath` values like "/members" and live in
   * the database for the retention window; the platform console deep-links in here; people
   * bookmark. A redirect pointing at a path that no longer exists is a Not Found that reads as
   * "the feature was deleted".
   */
  it('each point at a section that exists', () => {
    const sections = new Set(SETTINGS_SECTIONS.map((s) => s.to));
    for (const [from, to] of Object.entries(LEGACY_SETTINGS_PATHS)) {
      expect(sections.has(to), `${from} redirects to ${to}, which is not a section`).toBe(true);
    }
  });

  it('are not guarded, because the section they redirect to is', () => {
    // A guard here would refuse before the redirect ran, so an old bookmark would show the
    // refusal page rather than the page it was bookmarked for.
    const guarded = Object.keys(LEGACY_SETTINGS_PATHS).filter((p) => ROUTE_GUARDS[p]);
    expect(guarded).toEqual([]);
  });

  it('are gone from the sidebar', () => {
    const stale = NAV.filter((i) => i.to in LEGACY_SETTINGS_PATHS);
    expect(stale.map((i) => i.to)).toEqual([]);
  });
});
