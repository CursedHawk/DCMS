import { LogOut, Moon, Sun } from 'lucide-react';
import { Button, useTheme } from '@dcms/admin-ui';
import { logout } from '../auth';
import { runtimeConfig } from '../runtime-config';
import type { PlatformMe } from '../lib/permissions';

export function Topbar({ me }: { me: PlatformMe | undefined }) {
  const { resolved, setTheme } = useTheme();

  return (
    <header className="flex h-12 shrink-0 items-center justify-between gap-4 border-b border-border px-4">
      <a
        href={runtimeConfig.adminBase}
        className="truncate text-sm text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
      >
        Open tenant admin
      </a>

      <div className="flex items-center gap-1">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => setTheme(resolved === 'dark' ? 'light' : 'dark')}
          aria-label={resolved === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
        >
          {resolved === 'dark' ? <Sun className="h-4 w-4" /> : <Moon className="h-4 w-4" />}
        </Button>

        {me?.email && (
          <span
            className="hidden max-w-[16rem] truncate px-2 text-sm text-muted-foreground sm:inline"
            title={me.roles.join(', ')}
          >
            {me.email}
          </span>
        )}

        <Button variant="ghost" size="sm" onClick={() => void logout()}>
          <LogOut className="h-4 w-4" />
          Sign out
        </Button>
      </div>
    </header>
  );
}
