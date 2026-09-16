import { FileCode, LayoutTemplate, Newspaper } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { CenteredSpinner, cn, Dialog, DialogContent, DialogHeader, DialogTitle } from '@dcms/ui';
import type { SiteTemplate } from '../site-source/ide';

/**
 * Shown once, when a brand-new React site is opened: which template to start from.
 *
 * <p>Every choice is a complete project with this tenant's typed API client already in it, and an
 * AGENTS.md that tells the assistant how the project is laid out. The choice is about what is on
 * the page on day one, not about whether the site can reach its content.</p>
 *
 * <p>There is deliberately no local fallback. The editor used to seed its own hard-coded project
 * when generation failed — a fourth template, on different dependency versions, with no API
 * client — so a network error silently produced a different kind of site. Now a failure says so
 * and offers to try again.</p>
 */
export function StarterPicker({
  open,
  pending,
  failed,
  onPick,
}: {
  open: boolean;
  pending: boolean;
  failed: boolean;
  onPick: (template: SiteTemplate) => void;
}) {
  const { t } = useTranslation();

  const options: { template: SiteTemplate; icon: typeof FileCode; title: string; desc: string }[] = [
    {
      template: 'content',
      icon: Newspaper,
      title: t('ide.starter.contentTitle'),
      desc: t('ide.starter.contentDesc'),
    },
    {
      template: 'landing',
      icon: LayoutTemplate,
      title: t('ide.starter.landingTitle'),
      desc: t('ide.starter.landingDesc'),
    },
    {
      template: 'blank',
      icon: FileCode,
      title: t('ide.starter.blankTitle'),
      desc: t('ide.starter.blankDesc'),
    },
  ];

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        // Dismissing is a choice too: the smallest project, rather than an editor with no files.
        if (!o && !pending) onPick('blank');
      }}
    >
      <DialogContent className="max-w-lg" onInteractOutside={(e) => e.preventDefault()}>
        <DialogHeader>
          <DialogTitle>{t('ide.starter.title')}</DialogTitle>
        </DialogHeader>
        {pending ? (
          <CenteredSpinner label={t('ide.starter.generating')} />
        ) : (
          <div className="grid gap-2">
            {failed && (
              <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive">
                {t('ide.starter.failed')}
              </p>
            )}
            {options.map((o) => (
              <button
                key={o.template}
                type="button"
                onClick={() => onPick(o.template)}
                className={cn(
                  'flex items-start gap-3 rounded-lg border p-3 text-left transition-colors',
                  'hover:border-primary hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                )}
              >
                <div className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-accent text-accent-foreground">
                  <o.icon className="h-5 w-5" />
                </div>
                <div className="min-w-0">
                  <p className="font-medium">{o.title}</p>
                  <p className="text-sm text-muted-foreground">{o.desc}</p>
                </div>
              </button>
            ))}
            <p className="px-1 pt-1 text-xs text-muted-foreground">{t('ide.starter.footnote')}</p>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

