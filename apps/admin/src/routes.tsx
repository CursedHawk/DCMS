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
import { AccountPage } from './features/account/AccountPage';
import { AiSettingsPage } from './features/ai/AiSettingsPage';
import { ContentPage } from './features/content/ContentPage';
import { DashboardPage } from './features/dashboard/DashboardPage';
import { DomainsPage } from './features/domains/DomainsPage';
import { InviteAcceptPage } from './features/invitations/InviteAcceptPage';
import { MediaPage } from './features/media/MediaPage';
import { MembersPage } from './features/members/MembersPage';
import { PluginsPage } from './features/plugins/PluginsPage';
import { RolesPage } from './features/roles/RolesPage';
import { SitesPage } from './features/sites/SitesPage';
import { SiteWorkspace } from './features/sites/SiteWorkspace';
import { TenantsPage } from './features/tenants/TenantsPage';
import { completeSignin } from './auth';

// Heavy, route-specific pages are loaded on demand to keep the initial bundle lean.
const OpenApiPage = lazy(() => import('./features/openapi/OpenApiPage').then((m) => ({ default: m.OpenApiPage })));
const AnalyticsPage = lazy(() => import('./features/analytics/AnalyticsPage').then((m) => ({ default: m.AnalyticsPage })));
const ChatPage = lazy(() => import('./features/chat/ChatPage').then((m) => ({ default: m.ChatPage })));

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

function child(path: string, component: () => React.ReactNode) {
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
const sitesRoute = child('/sites', SitesPage);
const openapiRoute = child('/openapi', () => <OpenApiPage />);
const aiRoute = child('/ai', AiSettingsPage);
const accountRoute = child('/account', AccountPage);
const analyticsRoute = child('/analytics', () => <AnalyticsPage />);
const chatRoute = child('/chat', () => <ChatPage />);
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
    sitesRoute,
    editorRoute,
    openapiRoute,
    aiRoute,
    accountRoute,
    analyticsRoute,
    chatRoute,
    inviteRoute,
  ]),
]);
