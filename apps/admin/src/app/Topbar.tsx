import { Bot, Languages, Search, Settings } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  ThemeControl,
  Topbar as ShellTopbar,
  TourButton,
  UserMenu,
} from '@dcms/ui';
import { NotificationBell } from '../features/notifications/NotificationBell';
import { LANGUAGES, setLanguage } from '../lib/i18n';
import { logout } from '../auth';
import { useAuth } from '../useAuth';
import { useAi } from '../features/assistant/context';
import { TenantSwitcher } from './TenantSwitcher';

export function Topbar({
  onOpenSearch,
  menuButton,
}: {
  onOpenSearch: () => void;
  menuButton: React.ReactNode;
}) {
  const { t, i18n } = useTranslation();
  const { user } = useAuth();
  const navigate = useNavigate();
  const ai = useAi();

  return (
    <ShellTopbar
      start={
        <>
          {menuButton}
          <TenantSwitcher />

          <button
            type="button"
            onClick={onOpenSearch}
            className="hidden items-center gap-2 rounded-md border bg-muted/40 px-3 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-muted md:flex"
          >
            <Search className="h-4 w-4" aria-hidden />
            {t('actions.search')}
            <kbd className="ml-6 rounded border bg-background px-1.5 text-[10px]">⌘K</kbd>
          </button>

          {/* Below md the labelled search box does not fit; the shortcut is also unavailable
              on a touch keyboard, so search needs a real button of its own. */}
          <Button
            variant="ghost"
            size="icon"
            className="md:hidden"
            aria-label={t('actions.search')}
            onClick={onOpenSearch}
          >
            <Search className="h-4 w-4" aria-hidden />
          </Button>
        </>
      }
      end={
        <>
          {/* Enabled once there is a signed-in user: the bell's query and its hub connection
              both need a token and a tenant. */}
          {/* ⌘J, alongside ⌘K for navigation. The two get used in the same breath and
              stealing the shortcut people already know is how a new feature earns resentment. */}
          <Button
            variant="ghost"
            size="icon"
            aria-label={t('assistant.open')}
            onClick={() => ai.setOpen(true)}
          >
            <Bot className="h-4 w-4" aria-hidden />
          </Button>

          {/* Only appears on a page that has declared a tour. */}
          <TourButton />

          <NotificationBell enabled={!!user} />

          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" aria-label={t('nav.language')}>
                <Languages className="h-4 w-4" aria-hidden />
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

          <ThemeControl
            labels={{
              toggle: t('theme.toggle'),
              light: t('theme.light'),
              dark: t('theme.dark'),
              system: t('theme.system'),
            }}
          />

          <UserMenu
            name={user?.profile.name}
            email={user?.profile.email}
            menuLabel={t('account.menuItem')}
            signOutLabel={t('actions.signOut')}
            onSignOut={() => void logout()}
            entries={[
              {
                label: t('account.menuItem'),
                icon: Settings,
                onSelect: () => void navigate({ to: '/account' as string }),
              },
            ]}
          />
        </>
      }
    />
  );
}
