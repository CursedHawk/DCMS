import { Outlet, createRootRoute, createRoute, redirect, useParams } from '@tanstack/react-router';
import { lazy } from 'react';
import { RequirePermission } from '@dcms/ui';
import { AppShell } from './app/AppShell';
import type { RepoSearch } from './features/ide/RepoWorkspace';
import { LEGACY_PLUGIN_PATHS, LEGACY_SETTINGS_PATHS, ROUTE_GUARDS, type RouteGuard } from './app/routeGuards';
import { DashboardPage } from './features/dashboard/DashboardPage';

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
const AssistantPage = page(() => import('./features/assistant/AssistantPage'), 'AssistantPage');
const AuditPage = page(() => import('./features/audit/AuditPage'), 'AuditPage');
const ContentPage = page(() => import('./features/content/ContentPage'), 'ContentPage');
const DomainsPage = page(() => import('./features/domains/DomainsPage'), 'DomainsPage');
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
const JobsPage = page(() => import('./features/jobs/JobsPage'), 'JobsPage');
const DeploymentsPage = page(() => import('./features/environments/DeploymentsPage'), 'DeploymentsPage');
const OpenApiPage = page(() => import('./features/openapi/OpenApiPage'), 'OpenApiPage');
const ConnectionsPage = page(() => import('./features/connections/ConnectionsPage'), 'ConnectionsPage');
const PluginsPage = page(() => import('./features/plugins/PluginsPage'), 'PluginsPage');
const RolesPage = page(() => import('./features/roles/RolesPage'), 'RolesPage');
const SitesPage = page(() => import('./features/sites/SitesPage'), 'SitesPage');
const TenantsPage = page(() => import('./features/tenants/TenantsPage'), 'TenantsPage');
const SettingsIndex = page(() => import('./features/settings/SettingsIndex'), 'SettingsIndex');
const SettingsLayout = page(() => import('./features/settings/SettingsLayout'), 'SettingsLayout');
const UsagePage = page(() => import('./features/usage/UsagePage'), 'UsagePage');
const EnvironmentsPage = page(() => import('./features/environments/EnvironmentsPage'), 'EnvironmentsPage');
const WorkspacePage = page(() => import('./features/workspace/WorkspacePage'), 'WorkspacePage');

// These take a prop, so they cannot use the helper above.
const PluginReferencePage = lazy(() =>
  import('./features/marketplace/PluginReferencePage').then((m) => ({ default: m.PluginReferencePage })),
);
const AppPage = lazy(() => import('./features/apps/AppPage').then((m) => ({ default: m.AppPage })));
const PluginAppRedirect = lazy(() => import('./features/apps/AppPage').then((m) => ({ default: m.PluginAppRedirect })));
const RepoWorkspace = lazy(() =>
  import('./features/ide/RepoWorkspace').then((m) => ({ default: m.RepoWorkspace })),
);
const SiteWorkspace = lazy(() =>
  import('./features/sites/SiteWorkspace').then((m) => ({ default: m.SiteWorkspace })),
);

// Root renders just an outlet, and the authenticated shell is a pathless layout beneath it.
// That shape outlived its original reason — it existed so the OIDC callback route could render
// outside the shell, before a user existed. The edge owns the callback now (ADR 0014), so there
// is no /auth/callback here any more; the layout stays because the shell still decides between
// itself and the sign-in screen.
const rootRoute = createRootRoute({ component: () => <Outlet /> });

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
/** A plugin's developer reference: its contracts, events, data and how to use them from C#. */
const pluginReferenceRoute = createRoute({
  getParentRoute: () => appLayoutRoute,
  path: '/marketplace/$pluginId',
  component: function PluginReference() {
    const { pluginId } = useParams({ strict: false });
    return (
      <RequirePermission perm={ROUTE_GUARDS['/marketplace'].perm}>
        <PluginReferencePage pluginId={pluginId as string} />
      </RequirePermission>
    );
  },
});
/*
 * An enabled plugin instance is an app (#337): one frame whose sub-navigation is the plugin's
 * screens, its content collections and, under Admin, its settings and the platform's pages of it.
 * No route guard: the frame lists only what the member may open, and each screen is guarded by
 * its own permission.
 */
function appRoute(path: string) {
  return createRoute({
    getParentRoute: () => appLayoutRoute,
    path,
    component: function App() {
      const { slug, page, type } = useParams({ strict: false }) as { slug: string; page?: string; type?: string };
      return <AppPage key={slug} slug={slug} page={type ? 'content' : page} type={type} />;
    },
  });
}
const appIndexRoute = appRoute('/apps/$slug');
const appPageRoute = appRoute('/apps/$slug/$page');
const appContentRoute = appRoute('/apps/$slug/content/$type');

/*
 * Where an instance and its screens lived before. Job links and notification rows in the database
 * still carry these, and people bookmark: `/plugins/{slug}?tab=…` was a tab of the instance page,
 * `/plugins/{slug}/{screen}` one of the plugin's screens on it.
 */
const legacyInstanceRoute = createRoute({
  getParentRoute: () => appLayoutRoute,
  path: '/plugins/$slug',
  beforeLoad: ({ params, search }) => {
    const { tab, ...rest } = search as { tab?: string };
    const page = tab && tab !== 'overview' ? `/${tab}` : '';
    throw redirect({ to: `/apps/${params.slug}${page}` as string, search: rest as never, replace: true });
  },
});
const legacyInstanceScreenRoute = createRoute({
  getParentRoute: () => appLayoutRoute,
  path: '/plugins/$slug/$screen',
  beforeLoad: ({ params, search }) => {
    throw redirect({ to: `/apps/${params.slug}/${params.screen}` as string, search: search as never, replace: true });
  },
});
/** A plugin-wide screen's page before it was a screen of each app: the first enabled instance's. */
const legacyPluginAppRoute = createRoute({
  getParentRoute: () => appLayoutRoute,
  path: '/app/$pluginId/$screen',
  component: function LegacyPluginApp() {
    const { pluginId, screen } = useParams({ strict: false });
    return <PluginAppRedirect pluginId={pluginId as string} screenId={screen as string} />;
  },
});
const mediaRoute = child('/media', MediaPage);
const sitesRoute = child('/sites', SitesPage);
const accountRoute = child('/account', AccountPage);
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
const notificationsRoute = child('/notifications', NotificationsPage);
const jobsRoute = child('/jobs', JobsPage);
const deploymentsRoute = child('/deployments', DeploymentsPage);
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
const settingsConnectionsRoute = settingsChild('connections', ConnectionsPage);
const settingsUsageRoute = settingsChild('usage', UsagePage);
const settingsEnvironmentsRoute = settingsChild('environments', EnvironmentsPage);

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

/*
 * Screens that moved into their plugins (ADR 0019) keep their old URLs, query string included:
 * notification rows link to /chat?conversation=… and /forms?instance=….
 */
const legacyPluginRoutes = Object.entries(LEGACY_PLUGIN_PATHS).map(([from, to]) =>
  createRoute({
    getParentRoute: () => appLayoutRoute,
    path: from,
    beforeLoad: ({ search }) => {
      throw redirect({ to: to as string, search: search as never, replace: true });
    },
  }),
);

const repoEditorRoute = createRoute({
  getParentRoute: () => appLayoutRoute,
  path: '/repos/$repoId/code',
  validateSearch: (search: Record<string, unknown>): RepoSearch => {
    const text = (key: string) => (typeof search[key] === 'string' ? (search[key] as string) : undefined);
    return { name: text('name'), slug: text('slug'), kind: text('kind') };
  },
  component: function RepoEditor() {
    const { repoId } = useParams({ strict: false });
    const search = repoEditorRoute.useSearch();
    return <RepoWorkspace repoId={repoId as string} search={search} />;
  },
});

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
  appLayoutRoute.addChildren([
    indexRoute,
    tenantsRoute,
    pluginsRoute,
    appIndexRoute,
    appPageRoute,
    appContentRoute,
    legacyInstanceRoute,
    legacyInstanceScreenRoute,
    legacyPluginAppRoute,
    contentRoute,
    marketplaceRoute,
    pluginReferenceRoute,
    mediaRoute,
    sitesRoute,
    editorRoute,
    repoEditorRoute,
    accountRoute,
    assistantRoute,
    assistantChatRoute,
    notificationsRoute,
    jobsRoute,
    deploymentsRoute,
    inviteRoute,
    settingsRoute.addChildren([
      settingsIndexRoute,
      settingsGeneralRoute,
      settingsMembersRoute,
      settingsRolesRoute,
      settingsDomainsRoute,
      settingsAiRoute,
      settingsAuditRoute,
      settingsConnectionsRoute,
      settingsUsageRoute,
      settingsEnvironmentsRoute,
      settingsApiRoute,
    ]),
    ...legacyRoutes,
    ...legacyPluginRoutes,
  ]),
]);
