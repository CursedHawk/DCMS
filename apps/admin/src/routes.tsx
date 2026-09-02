import {
  Outlet,
  createRootRoute,
  createRoute,
  useNavigate,
  useParams,
} from '@tanstack/react-router';
import { lazy, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { AppShell } from './app/AppShell';
import { DashboardPage } from './features/dashboard/DashboardPage';
import { completeSignin } from './auth';

/*
 * Every page except the dashboard is loaded on demand.
 *
 * The initial download is then the shell plus the one page being opened, instead
 * of every page in the app: several pull large route-specific libraries — Monaco
 * and esbuild-wasm (site editor), GrapesJS (visual builder), Scalar (API docs),
 * recharts (analytics), SignalR (chat), react-jsonschema-form (plugin config) —
 * and statically importing any of them here puts them in the entry graph, where
 * the browser preloads them on the dashboard.
 *
 * AppShell already wraps <Outlet /> in a Suspense boundary, so these need no
 * per-route fallback.
 */
const page = <T extends string>(load: () => Promise<Record<T, React.FunctionComponent>>, name: T) =>
  lazy(() => load().then((m) => ({ default: m[name] })));

const AccountPage = page(() => import('./features/account/AccountPage'), 'AccountPage');
const AiSettingsPage = page(() => import('./features/ai/AiSettingsPage'), 'AiSettingsPage');
const AnalyticsPage = page(() => import('./features/analytics/AnalyticsPage'), 'AnalyticsPage');
const AuditPage = page(() => import('./features/audit/AuditPage'), 'AuditPage');
const ChatPage = page(() => import('./features/chat/ChatPage'), 'ChatPage');
const ContentPage = page(() => import('./features/content/ContentPage'), 'ContentPage');
const DomainsPage = page(() => import('./features/domains/DomainsPage'), 'DomainsPage');
const FormsPage = page(() => import('./features/forms/FormsPage'), 'FormsPage');
const InviteAcceptPage = page(() => import('./features/invitations/InviteAcceptPage'), 'InviteAcceptPage');
const MediaPage = page(() => import('./features/media/MediaPage'), 'MediaPage');
const MembersPage = page(() => import('./features/members/MembersPage'), 'MembersPage');
const NotificationsPage = page(
  () => import('./features/notifications/NotificationsPage'),
  'NotificationsPage',
);
const OpenApiPage = page(() => import('./features/openapi/OpenApiPage'), 'OpenApiPage');
const PluginsPage = page(() => import('./features/plugins/PluginsPage'), 'PluginsPage');
const RolesPage = page(() => import('./features/roles/RolesPage'), 'RolesPage');
const SitesPage = page(() => import('./features/sites/SitesPage'), 'SitesPage');
const TenantsPage = page(() => import('./features/tenants/TenantsPage'), 'TenantsPage');
const WorkspacePage = page(() => import('./features/workspace/WorkspacePage'), 'WorkspacePage');

// Takes a prop, so it cannot use the helper above.
const SiteWorkspace = lazy(() =>
  import('./features/sites/SiteWorkspace').then((m) => ({ default: m.SiteWorkspace })),
);

// Root renders just an outlet; the authenticated shell is a pathless layout so
// the OIDC callback can complete outside the shell (before a user exists).
const rootRoute = createRootRoute({ component: () => <Outlet /> });

const callbackRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/auth/callback',
  component: function Callback() {
    const navigate = useNavigate();
    const { t } = useTranslation();
    useEffect(() => {
      completeSignin()
        .catch((err) => console.error('OIDC sign-in callback failed', err))
        .finally(() => void navigate({ to: '/' }));
    }, [navigate]);
    return <p className="p-10 text-center text-sm text-muted-foreground">{t('auth.completing')}</p>;
  },
});

const appLayoutRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: 'app',
  component: AppShell,
});

// FunctionComponent rather than a plain function type: every page here is a
// React.lazy wrapper, which is an exotic component object, not a function, and
// the router's RouteComponent rejects class components.
function child(path: string, component: React.FunctionComponent) {
  return createRoute({ getParentRoute: () => appLayoutRoute, path, component });
}

const indexRoute = child('/', DashboardPage);
const tenantsRoute = child('/tenants', TenantsPage);
const membersRoute = child('/members', MembersPage);
const rolesRoute = child('/roles', RolesPage);
const domainsRoute = child('/domains', DomainsPage);
const pluginsRoute = child('/plugins', PluginsPage);
const contentRoute = child('/content', ContentPage);
const mediaRoute = child('/media', MediaPage);
const formsRoute = child('/forms', FormsPage);
const sitesRoute = child('/sites', SitesPage);
const openapiRoute = child('/openapi', OpenApiPage);
const aiRoute = child('/ai', AiSettingsPage);
const accountRoute = child('/account', AccountPage);
const workspaceRoute = child('/workspace', WorkspacePage);
const analyticsRoute = child('/analytics', AnalyticsPage);
const auditRoute = child('/audit', AuditPage);
const chatRoute = child('/chat', ChatPage);
const notificationsRoute = child('/notifications', NotificationsPage);
const inviteRoute = child('/invite/accept', InviteAcceptPage);

const editorRoute = createRoute({
  getParentRoute: () => appLayoutRoute,
  path: '/sites/$siteId',
  component: function Editor() {
    const { siteId } = useParams({ strict: false });
    return <SiteWorkspace siteId={siteId as string} />;
  },
});

export const routeTree = rootRoute.addChildren([
  callbackRoute,
  appLayoutRoute.addChildren([
    indexRoute,
    tenantsRoute,
    membersRoute,
    rolesRoute,
    domainsRoute,
    pluginsRoute,
    contentRoute,
    mediaRoute,
    formsRoute,
    sitesRoute,
    editorRoute,
    openapiRoute,
    aiRoute,
    accountRoute,
    workspaceRoute,
    analyticsRoute,
    auditRoute,
    chatRoute,
    notificationsRoute,
    inviteRoute,
  ]),
]);
