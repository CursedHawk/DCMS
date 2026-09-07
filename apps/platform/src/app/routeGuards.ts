import { Perm } from '../lib/permissions';

export interface RouteGuard {
  perm?: string;
  superAdmin?: boolean;
}

/**
 * What each page requires, as data, so that `nav.tsx` and `routes.tsx` can be checked against
 * each other. See the admin SPA's copy for the reasoning; the failure mode is the same in both
 * consoles and it is silent in both.
 */
export const ROUTE_GUARDS: Readonly<Record<string, RouteGuard>> = {
  '/': { perm: Perm.OverviewRead },
  '/tenants': { perm: Perm.TenantsRead },
  '/users': { perm: Perm.UsersRead },
  '/audit': { perm: Perm.AuditRead },
  '/monitoring': { perm: Perm.ObservabilityRead },
  '/storage': { perm: Perm.LogsRead },
  '/access': { perm: Perm.RolesManage },
  '/certificates': { perm: Perm.CertificatesManage },
  // Reached from the bell rather than the sidebar: it is per-operator, not an area of the
  // platform, and every nav item here names a permission this one would have to invent.
  '/notifications': { superAdmin: true },
};
