import { describe, expect, it } from 'vitest';
import { NAV } from './nav';
import { OPEN_ROUTES, ROUTE_GUARDS } from './routeGuards';

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
