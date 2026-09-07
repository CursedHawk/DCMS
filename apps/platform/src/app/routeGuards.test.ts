import { describe, expect, it } from 'vitest';
import { NAV } from './nav';
import { ROUTE_GUARDS } from './routeGuards';

describe('nav and route guards agree', () => {
  it.each(NAV.map((i) => [i.to, i.perm] as const))('%s requires %s', (to, perm) => {
    expect(ROUTE_GUARDS[to]?.perm, `${to} is guarded on a different key than the nav filters on`)
      .toBe(perm);
  });

  it('guards every sidebar destination', () => {
    expect(NAV.filter((i) => !ROUTE_GUARDS[i.to]).map((i) => i.to)).toEqual([]);
  });

  it('guards the notifications page, which has no sidebar entry', () => {
    // Reached only from the bell, so the nav check above would never have covered it.
    expect(ROUTE_GUARDS['/notifications']).toEqual({ superAdmin: true });
  });

  it('leaves no route open — every page in this console names a permission', () => {
    // The admin SPA has genuinely open pages (the dashboard, your own account). This console
    // does not: an operator with no platform permission never gets past the shell's NoAccess.
    const open = Object.entries(ROUTE_GUARDS).filter(([, g]) => !g.perm && !g.superAdmin);
    expect(open).toEqual([]);
  });
});
