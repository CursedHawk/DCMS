import { CircleHelp } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@dcms/ui';
import { MINI_TOURS, type MiniTour } from './tours';

/** The builder's Help: its tour again, a short tour of one task, the keys. */
export function HelpMenu({ onTour, onShortcuts }: { onTour: (which: MiniTour | null) => void; onShortcuts: () => void }) {
  const { t } = useTranslation();
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button size="icon" variant="ghost" aria-label={t('visual.help.title')} title={t('visual.help.title')}>
          <CircleHelp className="h-4 w-4" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <DropdownMenuItem onSelect={() => onTour(null)}>{t('visual.help.builderTour')}</DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuLabel className="px-2 py-1 text-xs text-muted-foreground">{t('visual.help.tasks')}</DropdownMenuLabel>
        {MINI_TOURS.map((which) => (
          <DropdownMenuItem key={which} onSelect={() => onTour(which)}>
            {t(`visual.tour.${which}.name`)}
          </DropdownMenuItem>
        ))}
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={onShortcuts}>{t('ide.shortcuts.title')}</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
