import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Link,
  Outlet,
  createRootRoute,
  createRoute,
  redirect,
  useNavigate,
  useParams,
} from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { getAccessToken, login, logout, userManager } from './auth';
import { AgentConsole } from './chat/AgentConsole';
import { EditorPage } from './editor/EditorPage';
import { sitesApi, type SiteSummary } from './editor/api';
import { useAuth } from './useAuth';
import {
  adminHeaders,
  getCurrentTenantSlug,
  setCurrentTenantSlug,
  useMyTenants,
} from './tenants';

const adminApiBase = import.meta.env.VITE_ADMIN_API_BASE ?? '/api';

function TenantSwitcher() {
  const { user } = useAuth();
  const tenants = useMyTenants(!!user);
  const [selected, setSelected] = useState<string>(getCurrentTenantSlug() ?? '');

  useEffect(() => {
    if (!selected && tenants.data?.length) {
      const first = tenants.data[0].slug;
      setSelected(first);
      setCurrentTenantSlug(first);
    }
  }, [tenants.data, selected]);

  if (!user || !tenants.data?.length) return null;

  return (
    <select
      value={selected}
      onChange={(e) => {
        setSelected(e.target.value);
        setCurrentTenantSlug(e.target.value);
        window.location.reload();
      }}
      className="rounded border border-slate-300 px-2 py-1 text-sm"
    >
      {tenants.data.map((t) => (
        <option key={t.tenantId} value={t.slug}>
          {t.name}
        </option>
      ))}
    </select>
  );
}

const rootRoute = createRootRoute({
  component: () => {
    const { user, loading } = useAuth();
    return (
      <div className="min-h-screen bg-slate-50 text-slate-900">
        <header className="flex items-center justify-between border-b border-slate-200 bg-white px-6 py-3">
          <div className="flex items-center gap-4">
            <span className="text-lg font-semibold">DCMS Admin</span>
            {user ? (
              <nav className="flex gap-3 text-sm text-slate-600">
                <Link to="/" className="hover:text-slate-900">Dashboard</Link>
                <Link to="/sites" className="hover:text-slate-900">Sites</Link>
                <Link to="/chat" className="hover:text-slate-900">Chat</Link>
              </nav>
            ) : null}
          </div>
          <div className="flex items-center gap-3 text-sm">
            {loading ? null : user ? (
              <span className="flex items-center gap-3">
                <TenantSwitcher />
                <span className="text-slate-600">{user.profile.name ?? user.profile.email}</span>
                <button
                  type="button"
                  onClick={() => void logout()}
                  className="rounded bg-slate-900 px-3 py-1 text-white"
                >
                  Sign out
                </button>
              </span>
            ) : (
              <button
                type="button"
                onClick={() => void login()}
                className="rounded bg-slate-900 px-3 py-1 text-white"
              >
                Sign in
              </button>
            )}
          </div>
        </header>
        <main className="p-6">
          <Outlet />
        </main>
      </div>
    );
  },
});

function Dashboard() {
  const { user, loading } = useAuth();

  // Calls the admin-api protected /me endpoint with the access token —
  // verifies the SPA token validates on the resource server.
  const me = useQuery({
    queryKey: ['admin-me'],
    enabled: !!user,
    queryFn: async () => {
      const res = await fetch(`${adminApiBase}/admin/me`, { headers: await adminHeaders() });
      if (!res.ok) throw new Error(`me failed: ${res.status}`);
      return res.json();
    },
  });

  if (loading) return <p>Loading…</p>;
  if (!user) {
    return (
      <section>
        <h1 className="text-2xl font-bold">Welcome to DCMS</h1>
        <p className="mt-2 text-slate-600">Sign in to manage tenants, plugins and content.</p>
      </section>
    );
  }

  return (
    <section>
      <h1 className="text-2xl font-bold">Dashboard</h1>
      <p className="mt-2 text-slate-600">Signed in as {user.profile.email}.</p>
      <pre className="mt-4 rounded bg-white p-4 text-sm shadow">
        {me.isLoading ? 'calling /api/admin/me…' : JSON.stringify(me.data ?? me.error, null, 2)}
      </pre>
    </section>
  );
}

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: Dashboard,
});

// OIDC redirect target: completes the code exchange then returns home.
const callbackRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/auth/callback',
  component: () => {
    const navigate = useNavigate();
    useEffect(() => {
      userManager
        .signinRedirectCallback()
        .catch(() => undefined)
        .finally(() => void navigate({ to: '/' }));
    }, [navigate]);
    return <p>Completing sign-in…</p>;
  },
});

function SitesPage() {
  const navigate = useNavigate();
  const qc = useQueryClient();
  const [name, setName] = useState('');
  const sites = useQuery({ queryKey: ['sites'], queryFn: () => sitesApi.list() });

  async function create() {
    if (!name.trim()) return;
    const { id } = await sitesApi.create(name.trim());
    setName('');
    await qc.invalidateQueries({ queryKey: ['sites'] });
    void navigate({ to: '/sites/$siteId', params: { siteId: id } });
  }

  return (
    <section>
      <h1 className="text-2xl font-bold">Sites</h1>
      <div className="mt-3 flex gap-2">
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="New site name"
          className="rounded border border-slate-300 px-2 py-1" />
        <button type="button" onClick={create} className="rounded bg-slate-900 px-3 py-1 text-white">Create</button>
      </div>
      <ul className="mt-4 space-y-2">
        {(sites.data ?? []).map((s: SiteSummary) => (
          <li key={s.id}>
            <Link to="/sites/$siteId" params={{ siteId: s.id }} className="text-blue-600 hover:underline">
              {s.name}
            </Link>
            <span className="ml-2 text-xs text-slate-400">{s.renderMode}{s.activeBuildId ? ' · published' : ' · draft'}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

const sitesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/sites',
  component: SitesPage,
});

const editorRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/sites/$siteId',
  component: () => {
    const { siteId } = useParams({ from: '/sites/$siteId' });
    return <EditorPage siteId={siteId} />;
  },
});

const chatRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/chat',
  component: AgentConsole,
});

export const routeTree = rootRoute.addChildren([indexRoute, callbackRoute, sitesRoute, editorRoute, chatRoute]);

// Exported for potential route guards in later phases.
export function requireAuthLoader() {
  return async () => {
    const token = await getAccessToken();
    if (!token) {
      throw redirect({ to: '/' });
    }
  };
}
