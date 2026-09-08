import { useTranslation } from 'react-i18next';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@dcms/ui';
import { modifierLabel } from './commands';

/**
 * What the keyboard does in this editor.
 *
 * <p>Shortcuts that are only discoverable by knowing them already are shortcuts nobody uses.
 * This is reachable three ways — <code>⌘/</code>, the status bar, and the palette itself — so
 * that finding it does not require the thing it is documenting.</p>
 *
 * <p>The modifier is written for the platform, but every binding accepts <b>either</b> Meta or
 * Control: a Mac keyboard on Linux is ordinary, and a shortcut that silently refuses one of
 * them is a bug nobody can describe.</p>
 */
export function ShortcutSheet({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const { t } = useTranslation();
  const mod = modifierLabel();

  const groups: { title: string; rows: { keys: string; label: string }[] }[] = [
    {
      title: t('ide.shortcuts.navigate'),
      rows: [
        { keys: `${mod} P`, label: t('ide.palette.goToFile') },
        { keys: `${mod} ⇧ P`, label: t('ide.palette.runCommand') },
        { keys: `${mod} ⇧ F`, label: t('ide.search.title') },
        { keys: `${mod} ⇧ G`, label: t('ide.git.title') },
        { keys: `${mod} ⇧ M`, label: t('ide.problems.title') },
      ],
    },
    {
      title: t('ide.shortcuts.edit'),
      rows: [
        { keys: `${mod} S`, label: t('ide.shortcuts.saveNow') },
        { keys: `${mod} \\`, label: t('ide.shortcuts.togglePreview') },
      ],
    },
    {
      title: t('ide.shortcuts.help'),
      rows: [
        { keys: `${mod} ?`, label: t('ide.shortcuts.title') },
        { keys: 'Esc', label: t('ide.shortcuts.dismiss') },
      ],
    },
  ];

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t('ide.shortcuts.title')}</DialogTitle>
          <DialogDescription>{t('ide.shortcuts.subtitle')}</DialogDescription>
        </DialogHeader>

        <div className="space-y-5">
          {groups.map((group) => (
            <section key={group.title}>
              <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                {group.title}
              </h3>
              <dl className="space-y-1.5">
                {group.rows.map((row) => (
                  <div key={row.keys} className="flex items-baseline justify-between gap-4">
                    <dt className="min-w-0 truncate text-sm">{row.label}</dt>
                    <dd className="shrink-0">
                      <kbd className="rounded border bg-muted px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                        {row.keys}
                      </kbd>
                    </dd>
                  </div>
                ))}
              </dl>
            </section>
          ))}
        </div>
      </DialogContent>
    </Dialog>
  );
}
