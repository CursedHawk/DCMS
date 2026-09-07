import { Outlet, useRouterState } from '@tanstack/react-router';
import { AnimatePresence, motion } from 'framer-motion';
import { Sparkles } from 'lucide-react';
import { Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, CenteredSpinner } from '@dcms/ui';
import { useNotificationHub } from '../features/notifications/useNotificationHub';
import { useMyPermissions } from '../lib/permissions';
import { login, register } from '../auth';
import { useAuth } from '../useAuth';
import { CommandPalette } from './CommandPalette';
import { StorageNotice } from './StorageNotice';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';

const COLLAPSE_KEY = 'dcms.sidebar.collapsed';

export function AppShell() {
  const { user, loading } = useAuth();
  const me = useMyPermissions(!!user);
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem(COLLAPSE_KEY) === '1');
  const [paletteOpen, setPaletteOpen] = useState(false);
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  // One connection for the whole app, opened here rather than in the bell so that unmounting
  // the popover does not drop the socket. `sub` is the platform user id, which the toast
  // rule compares against a notification's actor to avoid toasting your own actions back.
  useNotificationHub(!!user, user?.profile.sub);

  if (loading) {
    return <CenteredSpinner />;
  }

  if (!user) {
    return <SignIn />;
  }

  const toggleCollapse = () => {
    setCollapsed((c) => {
      localStorage.setItem(COLLAPSE_KEY, c ? '0' : '1');
      return !c;
    });
  };

  return (
    <div className="flex h-screen overflow-hidden">
      <Sidebar me={me.data} collapsed={collapsed} onToggle={toggleCollapse} />
      <div className="flex min-w-0 flex-1 flex-col">
        <Topbar onOpenSearch={() => setPaletteOpen(true)} />
        <AnimatePresence mode="wait">
          <motion.main
            key={pathname}
            initial={{ opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: -6 }}
            transition={{ duration: 0.18, ease: 'easeOut' }}
            className="flex-1 overflow-y-auto"
          >
            <Suspense fallback={<CenteredSpinner />}>
              <Outlet />
            </Suspense>
          </motion.main>
        </AnimatePresence>
      </div>
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} me={me.data} />
      <StorageNotice />
    </div>
  );
}

function SignIn() {
  const { t } = useTranslation();
  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-background via-background to-accent/30 p-6">
      <motion.div
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        className="w-full max-w-sm rounded-xl border bg-card p-8 text-center shadow-xl"
      >
        <div className="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-xl bg-primary text-primary-foreground">
          <Sparkles className="h-6 w-6" />
        </div>
        <h1 className="text-xl font-bold">{t('auth.welcome')}</h1>
        <p className="mt-2 text-sm text-muted-foreground">{t('auth.signInPrompt')}</p>
        <Button className="mt-6 w-full" size="lg" onClick={() => void login()}>
          {t('actions.signIn')}
        </Button>
        <p className="mt-4 text-sm text-muted-foreground">
          {t('auth.noAccount')}{' '}
          <button
            type="button"
            onClick={() => void register()}
            className="font-medium text-primary underline-offset-4 hover:underline"
          >
            {t('actions.signUp')}
          </button>
        </p>
      </motion.div>
    </div>
  );
}
