import { ExternalLink } from 'lucide-react';
import { ThemeControl, Topbar as ShellTopbar, UserMenu } from '@dcms/ui';
import { NotificationBell } from '../features/notifications/NotificationBell';
import { logout } from '../auth';
import { runtimeConfig } from '../runtime-config';
import type { PlatformMe } from '../lib/permissions';

export function Topbar({ me, menuButton }: { me: PlatformMe | undefined; menuButton: React.ReactNode }) {
  return (
    <ShellTopbar
      start={
        <>
          {menuButton}
          <a
            href={runtimeConfig.adminBase}
            className="flex items-center gap-1.5 truncate text-sm text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
          >
            Open tenant admin
            <ExternalLink className="h-3.5 w-3.5 shrink-0" aria-hidden />
          </a>
        </>
      }
      end={
        <>
          {/* SuperAdmin only, matching the endpoints: these notifications are platform-wide
              facts and there is no tenant-scoped permission that should reach them. */}
          <NotificationBell enabled={me?.isSuperAdmin ?? false} />

          {/* A toggle rather than the admin's three-way menu. This console is opened, read and
              closed; the OS-following option is a preference you set once, in the app you
              live in. */}
          <ThemeControl variant="toggle" labels={{ light: 'Switch to light', dark: 'Switch to dark' }} />

          <UserMenu
            email={me?.email}
            secondary={me?.roles.length ? me.roles.join(', ') : undefined}
            onSignOut={() => void logout()}
          />
        </>
      }
    />
  );
}
