import { Outlet } from '@tanstack/react-router';
import { motion, useReducedMotion } from 'framer-motion';
import { Sparkles } from 'lucide-react';
import { Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  AppFrame,
  Button,
  CenteredSpinner,
  PermissionProvider,
  TourOverlay,
  TourProvider,
  useSidebarCollapse,
} from '@dcms/ui';
import { useNotificationHub } from '../features/notifications/useNotificationHub';
import { useMyPermissions } from '../lib/permissions';
import { login, register } from '../auth';
import { useAuth } from '../useAuth';
import { CommandPalette } from './CommandPalette';
import { StorageNotice } from './StorageNotice';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';

export function AppShell() {
  const { t } = useTranslation();
  const { user, loading } = useAuth();
  const me = useMyPermissions(!!user);
  const [collapsed, toggleCollapse] = useSidebarCollapse();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);

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

  return (
    <PermissionProvider value={me.data}>
      <TourProvider
        labels={{
          next: t('tour.next'),
          back: t('tour.back'),
          done: t('tour.done'),
          skip: t('tour.skip'),
          start: t('tour.start'),
          dialog: t('tour.dialog'),
          progress: (current, total) => t('tour.progress', { current, total }),
        }}
      >
        <AppFrame
          navLabel={t('nav.sections')}
          menuLabel={t('nav.openMenu')}
          drawerOpen={drawerOpen}
          onDrawerOpenChange={setDrawerOpen}
          sidebar={({ inDrawer }) => (
            <Sidebar
              collapsed={collapsed}
              onToggle={toggleCollapse}
              inDrawer={inDrawer}
              onNavigate={inDrawer ? () => setDrawerOpen(false) : undefined}
            />
          )}
          topbar={({ menuButton }) => (
            <Topbar onOpenSearch={() => setPaletteOpen(true)} menuButton={menuButton} />
          )}
        >
          {/*
           * No page transition.
           *
           * There used to be an AnimatePresence fade-and-slide keyed on the pathname, which put
           * ~180ms of movement between every click and the page arriving — on every navigation,
           * carrying no information about what had changed. Motion in this app now answers an
           * action or reports a server-side change; moving the whole page because you clicked a
           * link does neither.
           */}
          <Suspense fallback={<CenteredSpinner />}>
            <Outlet />
          </Suspense>
        </AppFrame>

        <TourOverlay />
        <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      </TourProvider>
      <StorageNotice />
    </PermissionProvider>
  );
}

function SignIn() {
  const { t } = useTranslation();
  const reduced = useReducedMotion();

  return (
    <div className="flex min-h-dvh items-center justify-center bg-gradient-to-br from-background via-background to-accent/30 p-6">
      <motion.div
        initial={reduced ? false : { opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        className="w-full max-w-sm rounded-xl border bg-card p-8 text-center shadow-xl"
      >
        <div className="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-xl bg-primary text-primary-foreground">
          <Sparkles className="h-6 w-6" aria-hidden />
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
