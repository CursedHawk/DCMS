import { Languages, LogOut, Monitor, Moon, Search, Settings, Sun } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  Tooltip,
  TooltipContent,
  TooltipTrigger,
  useTheme,
} from '@dcms/admin-ui';
import { NotificationBell } from '../features/notifications/NotificationBell';
import { LANGUAGES, setLanguage } from '../lib/i18n';
import { logout } from '../auth';
import { useAuth } from '../useAuth';
import { TenantSwitcher } from './TenantSwitcher';

export function Topbar({ onOpenSearch }: { onOpenSearch: () => void }) {
  const { t, i18n } = useTranslation();
  const { user } = useAuth();
  const { theme, setTheme, resolved } = useTheme();
  const navigate = useNavigate();

  return (
    <header className="flex h-14 shrink-0 items-center gap-3 border-b bg-background/80 px-4 backdrop-blur">
      <TenantSwitcher />

      <button
        type="button"
        onClick={onOpenSearch}
        className="hidden items-center gap-2 rounded-md border bg-muted/40 px-3 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-muted sm:flex"
      >
        <Search className="h-4 w-4" />
        {t('actions.search')}
        <kbd className="ml-6 rounded border bg-background px-1.5 text-[10px]">⌘K</kbd>
      </button>

      <div className="flex-1" />

      {/* Notifications. Enabled once there is a signed-in user: the bell's query and hub
          connection both need a token and a tenant. */}
      <NotificationBell enabled={!!user} />

      {/* Language */}
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon" aria-label="Language">
            <Languages className="h-4 w-4" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          {LANGUAGES.map((l) => (
            <DropdownMenuItem
              key={l.code}
              onClick={() => setLanguage(l.code)}
              className={i18n.language === l.code ? 'font-semibold text-primary' : ''}
            >
              {l.label}
            </DropdownMenuItem>
          ))}
        </DropdownMenuContent>
      </DropdownMenu>

      {/* Theme. The tooltip and dropdown share one trigger: both are `asChild`
          Radix Slots nested onto the Button, so pointer handlers from both compose.
          (Wrapping the Button in a plain <Hint> here would swallow the dropdown's
          handlers and the menu would never open.) */}
      <DropdownMenu>
        <Tooltip>
          <TooltipTrigger asChild>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" aria-label={t('theme.toggle')}>
                {resolved === 'dark' ? <Moon className="h-4 w-4" /> : <Sun className="h-4 w-4" />}
              </Button>
            </DropdownMenuTrigger>
          </TooltipTrigger>
          <TooltipContent>{t('theme.toggle')}</TooltipContent>
        </Tooltip>
        <DropdownMenuContent align="end">
          <DropdownMenuItem onClick={() => setTheme('light')} className={theme === 'light' ? 'text-primary' : ''}>
            <Sun className="h-4 w-4" /> {t('theme.light')}
          </DropdownMenuItem>
          <DropdownMenuItem onClick={() => setTheme('dark')} className={theme === 'dark' ? 'text-primary' : ''}>
            <Moon className="h-4 w-4" /> {t('theme.dark')}
          </DropdownMenuItem>
          <DropdownMenuItem onClick={() => setTheme('system')} className={theme === 'system' ? 'text-primary' : ''}>
            <Monitor className="h-4 w-4" /> {t('theme.system')}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>

      {/* User */}
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <button
            type="button"
            className="flex h-8 w-8 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground"
          >
            {(user?.profile.name ?? user?.profile.email ?? '?').slice(0, 1).toUpperCase()}
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="min-w-56">
          <DropdownMenuLabel className="px-2 py-1.5">
            <p className="text-sm font-medium">{user?.profile.name ?? '—'}</p>
            <p className="text-xs text-muted-foreground">{user?.profile.email}</p>
          </DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuItem onClick={() => void navigate({ to: '/account' as string })}>
            <Settings className="h-4 w-4" /> {t('account.menuItem')}
          </DropdownMenuItem>
          <DropdownMenuItem destructive onClick={() => void logout()}>
            <LogOut className="h-4 w-4" /> {t('actions.signOut')}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </header>
  );
}
