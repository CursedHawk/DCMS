import { LogOut } from 'lucide-react';
import { Button } from '../ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '../ui/dropdown-menu';

export interface UserMenuEntry {
  label: string;
  icon?: React.ComponentType<{ className?: string }>;
  onSelect: () => void;
  destructive?: boolean;
}

/** The initial the avatar shows. Falls back through name, then email, then a question mark. */
function initial(name?: string | null, email?: string | null): string {
  return (name ?? email ?? '?').trim().slice(0, 1).toUpperCase();
}

export function UserMenu({
  name,
  email,
  secondary,
  entries = [],
  signOutLabel = 'Sign out',
  onSignOut,
  menuLabel = 'Account',
}: {
  name?: string | null;
  email?: string | null;
  /** A line under the email — the platform console shows the operator's global roles. */
  secondary?: string;
  entries?: readonly UserMenuEntry[];
  signOutLabel?: string;
  onSignOut: () => void;
  menuLabel?: string;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          aria-label={menuLabel}
          className="rounded-full bg-primary text-sm font-semibold text-primary-foreground hover:bg-primary/90"
        >
          {initial(name, email)}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-56">
        <DropdownMenuLabel className="px-2 py-1.5">
          <p className="text-sm font-medium">{name ?? email ?? '—'}</p>
          {name && email ? <p className="text-xs text-muted-foreground">{email}</p> : null}
          {secondary ? <p className="mt-0.5 text-xs text-muted-foreground">{secondary}</p> : null}
        </DropdownMenuLabel>
        {entries.length > 0 ? <DropdownMenuSeparator /> : null}
        {entries.map((e) => (
          <DropdownMenuItem key={e.label} destructive={e.destructive} onClick={e.onSelect}>
            {e.icon ? <e.icon className="h-4 w-4" /> : null}
            {e.label}
          </DropdownMenuItem>
        ))}
        <DropdownMenuSeparator />
        <DropdownMenuItem destructive onClick={onSignOut}>
          <LogOut className="h-4 w-4" /> {signOutLabel}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
