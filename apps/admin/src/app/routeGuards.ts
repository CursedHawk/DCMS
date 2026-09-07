import { Perm } from '../lib/permissions';

export interface RouteGuard {
  /** The permission key the page's endpoints are gated on. */
  perm?: string;
  /** Additionally require the platform SuperAdmin role. */
  superAdmin?: boolean;
}

/**
 * What each page requires, as data.
 *
 * Separate from `routes.tsx` so that it can be checked against `nav.tsx` — the sidebar filters
 * on one set of keys and the router guards on another, and the two silently disagreeing gives
 * you either a nav entry that leads to a refusal, or a page with no link that anyone holding
 * the right permission can still never find. `routeGuards.test.ts` fails the build when they
 * diverge.
 *
 * A path absent from this map is deliberately open to any signed-in member: the dashboard,
 * the account page, the notification list and the invitation-accept page, none of which have a
 * permission that would mean anything.
 */
export const ROUTE_GUARDS: Readonly<Record<string, RouteGuard>> = {
  '/tenants': { superAdmin: true },
  '/members': { perm: Perm.MembersManage },
  '/roles': { perm: Perm.RolesManage },
  '/domains': { perm: Perm.DomainsManage },
  '/plugins': { perm: Perm.PluginsManage },
  '/marketplace': { perm: Perm.PluginsManage },
  '/content': { perm: Perm.ContentRead },
  '/media': { perm: Perm.MediaRead },
  '/forms': { perm: Perm.ContentRead },
  '/sites': { perm: Perm.SiteEdit },
  '/sites/$siteId': { perm: Perm.SiteEdit },
  '/ai': { perm: Perm.AiSettings },
  '/workspace': { perm: Perm.TenantSettings },
  '/analytics': { perm: Perm.AnalyticsRead },
  '/audit': { perm: Perm.AuditRead },
  '/chat': { perm: Perm.ChatRead },
};

/** Paths reachable by any signed-in member, stated rather than merely omitted. */
export const OPEN_ROUTES: readonly string[] = [
  '/',
  '/openapi',
  '/account',
  '/notifications',
  '/invite/accept',
];
