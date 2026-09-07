import { Outlet } from '@tanstack/react-router';
import { Suspense } from 'react';
import { Button, CenteredSpinner } from '@dcms/ui';
import { login, logout } from '../auth';
import { useAuth } from '../useAuth';
import { useMe } from '../lib/permissions';
import { EnvironmentBand } from '../components/EnvironmentBand';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';

export function AppShell() {
  const { user, loading } = useAuth();
  const me = useMe(!!user);

  if (loading) return <CenteredSpinner />;
  if (!user) return <SignIn />;
  if (me.isLoading) return <CenteredSpinner />;

  // An authenticated account that holds no platform permission is the expected case for every
  // ordinary tenant user who finds this URL. Say so plainly rather than rendering an empty
  // shell they will read as broken.
  if (me.isError || !me.data || me.data.permissions.length === 0) {
    return <NoAccess />;
  }

  return (
    <div className="flex h-dvh flex-col overflow-hidden bg-background text-foreground md:flex-row">
      <Sidebar me={me.data} />
      <div className="flex min-w-0 flex-1 flex-col">
        <EnvironmentBand />
        <Topbar me={me.data} />
        <main className="flex-1 overflow-y-auto">
          <Suspense fallback={<CenteredSpinner />}>
            <Outlet />
          </Suspense>
        </main>
      </div>
    </div>
  );
}

function SignIn() {
  return (
    <Centered>
      <h1 className="text-lg font-semibold">DCMS Platform</h1>
      <p className="text-sm text-muted-foreground">
        The operations console for this platform. Sign in with a platform operator account.
      </p>
      <Button onClick={() => void login()} className="mt-2">Sign in</Button>
    </Centered>
  );
}

function NoAccess() {
  return (
    <Centered>
      <h1 className="text-lg font-semibold">This console is for platform operators</h1>
      <p className="text-sm text-muted-foreground">
        Your account is signed in but holds no platform permissions. If you manage a workspace,
        the tenant admin is where you want to be.
      </p>
      <Button variant="outline" className="mt-2" onClick={() => void logout()}>
        Sign in as someone else
      </Button>
    </Centered>
  );
}

function Centered({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex h-dvh items-center justify-center bg-background p-6 text-foreground">
      <div className="flex w-full max-w-sm flex-col items-start gap-2 rounded-md border border-border bg-card p-6">
        {children}
      </div>
    </div>
  );
}
