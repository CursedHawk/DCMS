import { useNavigate } from '@tanstack/react-router';
import { Command } from 'cmdk';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Settings } from 'lucide-react';
import { Dialog, DialogContent, usePermissions } from '@dcms/ui';
import { can } from '../lib/permissions';
import { NAV } from './nav';
import { SETTINGS_SECTIONS } from './routeGuards';

export function CommandPalette({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
}) {
  const { t } = useTranslation();
  const me = usePermissions();
  const navigate = useNavigate();

  // Global ⌘K / Ctrl+K shortcut.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key.toLowerCase() === 'k' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        onOpenChange(!open);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onOpenChange]);

  /*
   * The nav, plus every Settings section.
   *
   * Collapsing Members, Roles, Domains and the rest into one Settings entry made the sidebar
   * shorter and made them one keystroke further away — and this palette is precisely how people
   * reach a page whose name they know. Listing only "Settings" would mean typing "domains" found
   * nothing, which is a worse trade than the long sidebar was.
   */
  const items = [
    ...NAV.filter((i) => (i.superAdmin ? me?.isSuperAdmin : !i.perm || can(me, i.perm))).map(
      (i) => ({ to: i.to, labelKey: i.labelKey, icon: i.icon }),
    ),
    ...SETTINGS_SECTIONS.filter((s) => !s.perm || can(me, s.perm)).map((s) => ({
      to: s.to,
      labelKey: s.labelKey,
      icon: Settings,
    })),
  ];

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="overflow-hidden p-0" wide>
        <Command className="[&_[cmdk-input]]:h-12">
          <Command.Input
            placeholder={`${t('actions.search')}…`}
            className="w-full border-b bg-transparent px-4 text-sm outline-none placeholder:text-muted-foreground"
          />
          <Command.List className="max-h-80 overflow-y-auto p-2">
            <Command.Empty className="py-6 text-center text-sm text-muted-foreground">
              {t('common.noResults')}
            </Command.Empty>
            {items.map((item) => (
              <Command.Item
                key={item.to}
                value={t(item.labelKey)}
                onSelect={() => {
                  onOpenChange(false);
                  void navigate({ to: item.to as string });
                }}
                className="flex cursor-pointer items-center gap-3 rounded-md px-3 py-2 text-sm aria-selected:bg-accent aria-selected:text-accent-foreground"
              >
                <item.icon className="h-4 w-4" />
                {t(item.labelKey)}
              </Command.Item>
            ))}
          </Command.List>
        </Command>
      </DialogContent>
    </Dialog>
  );
}
