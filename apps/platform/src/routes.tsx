import { Outlet, createRootRoute, createRoute, useNavigate } from '@tanstack/react-router';
import { lazy, useEffect } from 'react';
import { RequirePermission } from '@dcms/ui';
import { AppShell } from './app/AppShell';
import { ROUTE_GUARDS, type RouteGuard } from './app/routeGuards';
import { completeSignin } from './auth';

/*
 * Every page is loaded on demand — the same reasoning as the admin SPA, with far less to
 * split: this console pulls in no editor, no chart runtime and no API-docs viewer.
 *
 * The overview is lazy too, including the page every visit starts on, and that is deliberate
 * rather than an oversight. Importing it here eagerly makes routes.tsx depend on a module that
 * depends on routes.tsx for its `Link` types, and TypeScript resolves that cycle by inferring
 * a route union containing only the routes it had reached — so `to="/tenants"` stops
 * type-checking in the one file that is meant to link everywhere. The chunk is 4 KB.
 */
const page = <T extends string>(load: () => Promise<Record<T, React.FunctionComponent>>, name: T) =>
  lazy(() => load().then((m) => ({ default: m[name] })));

const OverviewPage = page(() => import('./features/overview/OverviewPage'), 'OverviewPage');
const TenantsPage = page(() => import('./features/tenants/TenantsPage'), 'TenantsPage');
const UsersPage = page(() => import('./features/users/UsersPage'), 'UsersPage');
const AuditPage = page(() => import('./features/audit/AuditPage'), 'AuditPage');
const MonitoringPage = page(() => import('./features/monitoring/MonitoringPage'), 'MonitoringPage');
const StoragePage = page(() => import('./features/storage/StoragePage'), 'StoragePage');
const AccessPage = page(() => import('./features/access/AccessPage'), 'AccessPage');
const CertificatesPage = page(
  () => import('./features/certificates/CertificatesPage'),
  'CertificatesPage',
);
const NotificationsPage = page(
  () => import('./features/notifications/NotificationsPage'),
  'NotificationsPage',
);

const rootRoute = createRootRoute({ component: () => <Outlet /> });

// A sibling of the shell, not a child: the OIDC callback has to complete before a user exists.
const callbackRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/auth/callback',
  component: function Callback() {
    const navigate = useNavigate();
    useEffect(() => {
      completeSignin()
        .catch((err) => console.error('OIDC sign-in callback failed', err))
        .finally(() => void navigate({ to: '/' }));
    }, [navigate]);
    return <p className="p-10 text-center text-sm text-muted-foreground">Signing in…</p>;
  },
});

const appLayoutRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: 'app',
  component: AppShell,
});

/*
 * A page, and the permission it needs.
 *
 * The sidebar already filters on the same keys, but a route is reachable by typing its URL and
 * the sidebar is not access control. The page is not rendered at all now, and the refusal says
 * which permission is missing.
 *
 * Generic over the literal path, not `path: string`. Widening it to `string` collapses every
 * route to the same type, and the router's `to` union degrades to the routes it could still
 * discriminate — so `<Link to="/tenants">` stops type-checking while the app works fine at
 * runtime, which is the least useful combination available.
 */
const child = <TPath extends string>(path: TPath, component: React.FunctionComponent) => {
  const guard: RouteGuard | undefined = ROUTE_GUARDS[path];
  const Component = component;
  return createRoute({
    getParentRoute: () => appLayoutRoute,
    path,
    component: guard
      ? function Guarded() {
          return (
            <RequirePermission perm={guard.perm} superAdmin={guard.superAdmin}>
              <Component />
            </RequirePermission>
          );
        }
      : component,
  });
};

export const routeTree = rootRoute.addChildren([
  callbackRoute,
  appLayoutRoute.addChildren([
    child('/', OverviewPage),
    child('/tenants', TenantsPage),
    child('/users', UsersPage),
    child('/audit', AuditPage),
    child('/monitoring', MonitoringPage),
    child('/storage', StoragePage),
    child('/access', AccessPage),
    child('/certificates', CertificatesPage),
    // Reached from the bell rather than the sidebar: it is per-operator, not an area of the
    // platform, and every nav item here names a permission this one would have to invent.
    child('/notifications', NotificationsPage),
  ]),
]);
