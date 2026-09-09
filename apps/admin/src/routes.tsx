import {
  Outlet,
  createRootRoute,
  createRoute,
  redirect,
  useNavigate,
  useParams,
} from '@tanstack/react-router';
import { lazy, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { RequirePermission } from '@dcms/ui';
import { AppShell } from './app/AppShell';
import { LEGACY_SETTINGS_PATHS, ROUTE_GUARDS, type RouteGuard } from './app/routeGuards';
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
const AssistantPage = page(() => import('./features/assistant/AssistantPage'), 'AssistantPage');
const AuditPage = page(() => import('./features/audit/AuditPage'), 'AuditPage');
const ChatPage = page(() => import('./features/chat/ChatPage'), 'ChatPage');
const ContentPage = page(() => import('./features/content/ContentPage'), 'ContentPage');
const DomainsPage = page(() => import('./features/domains/DomainsPage'), 'DomainsPage');
const FormsPage = page(() => import('./features/forms/FormsPage'), 'FormsPage');
const InviteAcceptPage = page(
  () => import('./features/invitations/InviteAcceptPage'),
  'InviteAcceptPage',
);
const MarketplacePage = page(
  () => import('./features/marketplace/MarketplacePage'),
  'MarketplacePage',
);
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
const SettingsIndex = page(() => import('./features/settings/SettingsIndex'), 'SettingsIndex');
const SettingsLayout = page(() => import('./features/settings/SettingsLayout'), 'SettingsLayout');
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

/*
 * A page, and the permission it needs.
 *
 * Filtering the sidebar is not access control: every route here is reachable by typing its
 * URL, and before this a hidden page still rendered — then fired queries the API refused and
 * left a screen of failed requests and empty tables. The guard renders the refusal instead,
 * and names the permission so the reader knows what to ask for.
 *
 * The permission on each route is the one that gates the endpoints that page actually calls,
 * so this list and `app/nav.tsx` have to agree; `routes.test.ts` checks that they do.
 *
 * FunctionComponent rather than a plain function type: every page is a `React.lazy` wrapper,
 * which is an exotic component object rather than a function, and the router's RouteComponent
 * rejects class components.
 */
function child(path: string, component: React.FunctionComponent) {
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
}

const indexRoute = child('/', DashboardPage);
const tenantsRoute = child('/tenants', TenantsPage);
const pluginsRoute = child('/plugins', PluginsPage);
const contentRoute = child('/content', ContentPage);
const marketplaceRoute = child('/marketplace', MarketplacePage);
const mediaRoute = child('/media', MediaPage);
const formsRoute = child('/forms', FormsPage);
const sitesRoute = child('/sites', SitesPage);
const accountRoute = child('/account', AccountPage);
const analyticsRoute = child('/analytics', AnalyticsPage);
const assistantRoute = child('/assistant', AssistantPage);

/*
 * A conversation has its own URL so it can be linked to — which is the whole point of sharing
 * one. Same component: the route parameter is the only difference, and the page treats "no id"
 * as "a conversation that has not been started yet".
 */
const assistantChatRoute = createRoute({
  getParentRoute: () => appLayoutRoute,
  path: '/assistant/$conversationId',
  component: function AssistantChat() {
    return (
      <RequirePermission perm={ROUTE_GUARDS['/assistant'].perm}>
        <AssistantPage />
      </RequirePermission>
    );
  },
});
const chatRoute = child('/chat', ChatPage);
const notificationsRoute = child('/notifications', NotificationsPage);
const inviteRoute = child('/invite/accept', InviteAcceptPage);

/*
 * Settings: one destination with its own sub-navigation, rather than six sitting beside Content
 * and Media. The layout draws the sub-nav; each section is guarded individually through the same
 * `child()` helper, from the same list the sub-nav is built from (see routeGuards.ts).
 */
const settingsRoute = createRoute({
  getParentRoute: () => appLayoutRoute,
  path: '/settings',
  component: SettingsLayout,
});

/** A section of Settings. Paths are relative to `/settings`; guards are looked up absolute. */
function settingsChild(path: string, component: React.FunctionComponent) {
  const guard: RouteGuard | undefined = ROUTE_GUARDS[`/settings/${path}`];
  const Component = component;
  return createRoute({
    getParentRoute: () => settingsRoute,
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
}

const settingsIndexRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: '/',
  component: SettingsIndex,
});
const settingsGeneralRoute = settingsChild('general', WorkspacePage);
const settingsMembersRoute = settingsChild('members', MembersPage);
const settingsRolesRoute = settingsChild('roles', RolesPage);
const settingsDomainsRoute = settingsChild('domains', DomainsPage);
const settingsAiRoute = settingsChild('ai', AiSettingsPage);
const settingsAuditRoute = settingsChild('audit', AuditPage);
const settingsApiRoute = settingsChild('api', OpenApiPage);

/*
 * The old top-level URLs, kept alive as redirects.
 *
 * Not politeness. Notification rows carry `linkPath` values like `/members` and live in the
 * database for the length of the retention window; the platform console deep-links in here; and
 * people bookmark. Removing the routes would turn all of that into a Not Found that reads as
 * "the feature was deleted" rather than "it moved".
 */
const legacyRoutes = Object.entries(LEGACY_SETTINGS_PATHS).map(([from, to]) =>
  createRoute({
    getParentRoute: () => appLayoutRoute,
    path: from,
    beforeLoad: () => {
      throw redirect({ to: to as string, replace: true });
    },
  }),
);

const editorRoute = createRoute({
  getParentRoute: () => appLayoutRoute,
  path: '/sites/$siteId',
  component: function Editor() {
    const { siteId } = useParams({ strict: false });
    return (
      <RequirePermission perm={ROUTE_GUARDS['/sites/$siteId'].perm}>
        <SiteWorkspace siteId={siteId as string} />
      </RequirePermission>
    );
  },
});

export const routeTree = rootRoute.addChildren([
  callbackRoute,
  appLayoutRoute.addChildren([
    indexRoute,
    tenantsRoute,
    pluginsRoute,
    contentRoute,
    marketplaceRoute,
    mediaRoute,
    formsRoute,
    sitesRoute,
    editorRoute,
    accountRoute,
    analyticsRoute,
    assistantRoute,
    assistantChatRoute,
    chatRoute,
    notificationsRoute,
    inviteRoute,
    settingsRoute.addChildren([
      settingsIndexRoute,
      settingsGeneralRoute,
      settingsMembersRoute,
      settingsRolesRoute,
      settingsDomainsRoute,
      settingsAiRoute,
      settingsAuditRoute,
      settingsApiRoute,
    ]),
    ...legacyRoutes,
  ]),
]);
