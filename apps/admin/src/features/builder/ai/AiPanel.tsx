import type { DcmsComponentSpec } from '@dcms/gjs-schema';
import { useMutation } from '@tanstack/react-query';
import { AlertTriangle, Sparkles } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Button,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
} from '@dcms/admin-ui';
import { useBuilder } from '../store';
import { UnusableGenerationError, aiApi, type GeneratedSite } from './api';
import {
  appendBlock,
  applySite,
  replacePageSource,
  siteProblems,
  summarize,
  vetSiteFiles,
} from './apply';

/**
 * AI generation over the Mode A source.
 *
 * Three scopes, in increasing order of how much they overwrite: add a section to
 * this page, rewrite this page, or generate a whole site. The first applies
 * immediately — it only adds, and undo is a click away. The other two replace
 * work, so they say what they are about to destroy first.
 *
 * Everything lands in the working draft, which means the review step is the one
 * the author already has: the Source Control panel's diff.
 */

type Scope = 'block' | 'page' | 'site';

export function AiPanel({
  open,
  onOpenChange,
  specs,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  specs: readonly DcmsComponentSpec[];
}) {
  const { t } = useTranslation();
  const [scope, setScope] = useState<Scope>('block');
  const [instruction, setInstruction] = useState('');
  const [raw, setRaw] = useState<string | null>(null);
  const [pendingSite, setPendingSite] = useState<GeneratedSite | null>(null);

  const project = useBuilder((s) => s.project);
  const activeSlug = useBuilder((s) => s.activeSlug);
  const activePage = project?.pages.find((p) => p.entry.slug === activeSlug);

  const close = () => {
    onOpenChange(false);
    setRaw(null);
    setPendingSite(null);
  };

  const generate = useMutation({
    mutationFn: async () => {
      if (!project) throw new Error('no project');
      const context = { specs, theme: project.manifest.theme, pageHtml: activePage?.html };
      const prompt = instruction.trim();

      if (scope === 'site') return { kind: 'site' as const, site: await aiApi.site(prompt, context) };
      const source =
        scope === 'block' ? await aiApi.block(prompt, context) : await aiApi.page(prompt, context);
      return { kind: scope, source };
    },
    onSuccess: (result) => {
      setRaw(null);
      if (result.kind === 'site') {
        // A whole site replaces every page; the author confirms against a summary
        // of what will actually be written rather than against the raw response.
        setPendingSite(result.site);
        return;
      }
      if (!activeSlug) return;
      if (result.kind === 'block') appendBlock(activeSlug, result.source);
      else replacePageSource(activeSlug, result.source);
      toast.success(t('builder.ai.applied'));
      close();
    },
    onError: (error) => {
      if (error instanceof UnusableGenerationError) {
        // Show what came back instead of discarding it: the author waited for it,
        // and it is usually close enough to paste into the code view.
        setRaw(error.raw ?? '');
        toast.error(t('builder.ai.unusable'));
      } else {
        toast.error(t('errors.generic'));
      }
    },
  });

  const busy = generate.isPending;
  const canSubmit = !!instruction.trim() && !!project && !busy;

  return (
    <>
      <Dialog open={open} onOpenChange={(next) => (next ? onOpenChange(true) : close())}>
        <DialogContent className="max-w-xl">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <Sparkles className="h-4 w-4" /> {t('builder.ai.title')}
            </DialogTitle>
          </DialogHeader>

          <DialogBody className="space-y-3">
            <Tabs value={scope} onValueChange={(v) => setScope(v as Scope)}>
              <TabsList className="w-full">
                <TabsTrigger value="block" className="flex-1">
                  {t('builder.ai.scopeBlock')}
                </TabsTrigger>
                <TabsTrigger value="page" className="flex-1">
                  {t('builder.ai.scopePage')}
                </TabsTrigger>
                <TabsTrigger value="site" className="flex-1">
                  {t('builder.ai.scopeSite')}
                </TabsTrigger>
              </TabsList>

              <TabsContent value="block">
                <Hint>{t('builder.ai.hintBlock')}</Hint>
              </TabsContent>
              <TabsContent value="page">
                <Hint tone="warning">
                  {t('builder.ai.hintPage', { page: activePage?.entry.title ?? '' })}
                </Hint>
              </TabsContent>
              <TabsContent value="site">
                <Hint tone="warning">{t('builder.ai.hintSite')}</Hint>
              </TabsContent>
            </Tabs>

            <Textarea
              rows={5}
              value={instruction}
              onChange={(e) => setInstruction(e.target.value)}
              placeholder={t(`builder.ai.placeholder.${scope}`)}
            />

            {raw !== null && (
              <div className="space-y-1">
                <p className="text-xs text-muted-foreground">{t('builder.ai.rawOutput')}</p>
                <pre className="max-h-48 overflow-auto rounded border bg-muted/40 p-2 text-[11px]">
                  {raw || t('builder.ai.rawEmpty')}
                </pre>
              </div>
            )}
          </DialogBody>

          <DialogFooter>
            <Button variant="ghost" onClick={close} disabled={busy}>
              {t('actions.cancel')}
            </Button>
            <Button onClick={() => generate.mutate()} disabled={!canSubmit}>
              <Sparkles className="h-4 w-4" />
              {busy ? t('builder.ai.generating') : t('builder.ai.generate')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <SiteConfirm
        site={pendingSite}
        onCancel={() => setPendingSite(null)}
        onApply={(files) => {
          applySite(files);
          setPendingSite(null);
          toast.success(t('builder.ai.applied'));
          close();
        }}
      />
    </>
  );
}

function Hint({ children, tone }: { children: React.ReactNode; tone?: 'warning' }) {
  return (
    <p
      className={
        tone === 'warning'
          ? 'pt-2 text-xs text-amber-700 dark:text-amber-400'
          : 'pt-2 text-xs text-muted-foreground'
      }
    >
      {children}
    </p>
  );
}

/**
 * The confirmation for a whole-site generation.
 *
 * It vets before it asks: files outside a Mode A project are dropped, and a
 * manifest that would not open is a refusal rather than a warning. Replacing a
 * site on the strength of "it returned something" is exactly the accident this
 * screen exists to prevent.
 */
function SiteConfirm({
  site,
  onCancel,
  onApply,
}: {
  site: GeneratedSite | null;
  onCancel: () => void;
  onApply: (files: Record<string, string>) => void;
}) {
  const { t } = useTranslation();
  if (!site) return null;

  const { files, rejected } = vetSiteFiles(site);
  const problems = siteProblems(files);
  const { pages, styles } = summarize(files);

  return (
    <Dialog open onOpenChange={(next) => !next && onCancel()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{t('builder.ai.confirmSiteTitle')}</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-3">
          <p className="text-sm">{t('builder.ai.confirmSiteBody', { pages, styles })}</p>

          {problems.length > 0 && (
            <div className="space-y-1 rounded border border-destructive/40 bg-destructive/10 p-2 text-xs text-destructive">
              <p className="flex items-center gap-1.5 font-medium">
                <AlertTriangle className="h-3.5 w-3.5" /> {t('builder.ai.siteUnusable')}
              </p>
              {problems.map((problem) => (
                <p key={problem}>{problem}</p>
              ))}
            </div>
          )}

          {rejected.length > 0 && (
            <div className="space-y-1 rounded border bg-muted/40 p-2 text-xs text-muted-foreground">
              <p className="font-medium">{t('builder.ai.skippedFiles')}</p>
              {rejected.slice(0, 8).map((entry) => (
                <p key={entry.path}>
                  <code>{entry.path}</code> — {entry.reason}
                </p>
              ))}
            </div>
          )}

          <ul className="max-h-40 overflow-auto rounded border bg-muted/40 p-2 text-xs">
            {Object.keys(files)
              .sort()
              .map((path) => (
                <li key={path}>
                  <code>{path}</code>
                </li>
              ))}
          </ul>
        </DialogBody>
        <DialogFooter>
          <Button variant="ghost" onClick={onCancel}>
            {t('actions.cancel')}
          </Button>
          <Button
            variant="destructive"
            disabled={problems.length > 0}
            onClick={() => onApply(files)}
          >
            {t('builder.ai.replaceSite')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
