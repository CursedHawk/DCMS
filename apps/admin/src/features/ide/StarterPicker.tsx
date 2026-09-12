import { FileCode, FileJson, LayoutTemplate, Sparkles } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { CenteredSpinner, cn, Dialog, DialogContent, DialogHeader, DialogTitle } from '@dcms/ui';
export type StarterFlavor = 'empty' | 'openapi' | 'client' | 'starter';

// Shown once when a brand-new (empty) ReactApp site is opened: pick what to seed
// the workspace with. Everything but "empty" is generated from the tenant's
// content API (the same source as the API Docs tab).
export function StarterPicker({
  open,
  pending,
  onPick,
}: {
  open: boolean;
  pending: boolean;
  onPick: (flavor: StarterFlavor) => void;
}) {
  const { t } = useTranslation();

  const options: { flavor: StarterFlavor; icon: typeof FileCode; title: string; desc: string }[] = [
    {
      flavor: 'empty',
      icon: FileCode,
      title: t('ide.starter.emptyTitle', 'Empty project'),
      desc: t('ide.starter.emptyDesc', 'A minimal React + Vite app. Start from scratch.'),
    },
    {
      flavor: 'openapi',
      icon: FileJson,
      title: t('ide.starter.openapiTitle', 'OpenAPI document only'),
      desc: t('ide.starter.openapiDesc', "Just your tenant's content-API OpenAPI spec."),
    },
    {
      flavor: 'client',
      icon: Sparkles,
      title: t('ide.starter.clientTitle', 'Typed API client + OpenAPI'),
      desc: t(
        'ide.starter.clientDesc',
        'The generated typed TypeScript client and the OpenAPI spec.',
      ),
    },
    {
      flavor: 'starter',
      icon: LayoutTemplate,
      title: t('ide.starter.starterTitle', 'React starter + OpenAPI'),
      desc: t(
        'ide.starter.starterDesc',
        'A ready-to-run React site wired to your content API, plus the spec.',
      ),
    },
  ];

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        if (!o) onPick('empty');
      }}
    >
      <DialogContent className="max-w-lg" onInteractOutside={(e) => e.preventDefault()}>
        <DialogHeader>
          <DialogTitle>{t('ide.starter.title', 'Start your React app')}</DialogTitle>
        </DialogHeader>
        {pending ? (
          <CenteredSpinner label={t('ide.starter.generating', 'Generating…')} />
        ) : (
          <div className="grid gap-2">
            {options.map((o) => (
              <button
                key={o.flavor}
                type="button"
                onClick={() => onPick(o.flavor)}
                className={cn(
                  'flex items-start gap-3 rounded-lg border p-3 text-left transition-colors',
                  'hover:border-primary hover:bg-accent',
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
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
