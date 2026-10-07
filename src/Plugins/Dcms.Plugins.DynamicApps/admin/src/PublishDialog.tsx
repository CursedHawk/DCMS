import { AlertTriangle } from 'lucide-react';
import {
  Button,
  CenteredSpinner,
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  toast,
} from '@dcms/ui';
import { instancePath, usePluginApi, usePluginT } from '@dcms/plugin-ui';
import { useModelAction, usePreview, type ConfigChange, type ConfigIssue, type PublishResult, type RevisionInfo } from './api';

/**
 * Review, then publish: what the draft changes against the live app, which of those changes
 * existing data might not survive, and what still blocks it. Publishing is the one step that
 * changes the running app, so it is never a single click from anywhere else.
 */
export function PublishDialog({ slug, open, onOpenChange, draft }: {
  slug: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  draft: RevisionInfo;
}) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const preview = usePreview(slug, open);
  const publish = useModelAction(slug, () =>
    api.post<PublishResult>(instancePath(slug, '/_model/draft/publish'), { expectedHash: draft.hash }));

  const errors = preview.data?.issues.filter((i) => i.severity === 'error') ?? [];
  const warnings = preview.data?.issues.filter((i) => i.severity === 'warning') ?? [];

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl">
        <DialogHeader>
          <DialogTitle>{t('publish.title', { number: draft.number })}</DialogTitle>
          <DialogDescription>{t('publish.description')}</DialogDescription>
        </DialogHeader>
        <DialogBody className="max-h-[60vh] space-y-4 overflow-y-auto">
          {preview.isLoading || !preview.data ? (
            <CenteredSpinner />
          ) : (
            <>
              {preview.data.hasDestructiveChanges ? (
                <p className="flex items-start gap-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-3 text-sm">
                  <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden /> {t('publish.destructive')}
                </p>
              ) : null}
              <Issues issues={errors} />
              <Issues issues={warnings} />
              <ChangeList changes={preview.data.changes} empty={t('publish.noChanges')} />
            </>
          )}
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>{t('actions.cancel')}</Button>
          <Button
            disabled={!preview.data?.canPublish || publish.isPending}
            onClick={() => publish.mutate(undefined, {
              onSuccess: (result) => {
                if (result.published) {
                  toast.success(t('publish.done', { number: result.revision?.number }));
                  onOpenChange(false);
                }
              },
              onError: () => void preview.refetch(),
            })}
          >
            {t('actions.publish')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

export function Issues({ issues }: { issues: ConfigIssue[] }) {
  if (issues.length === 0) return null;
  return (
    <ul className="space-y-1 text-sm">
      {issues.map((issue, i) => (
        <li key={i} className={issue.severity === 'error' ? 'text-destructive' : 'text-amber-700 dark:text-amber-400'}>
          <span className="font-mono text-xs">{issue.path}</span> — {issue.message}
        </li>
      ))}
    </ul>
  );
}

const OP_MARK: Record<ConfigChange['op'], string> = { create: '+', update: '~', delete: '−' };

/** A change set as a reader wants it: `+ field deals.amount`, destructive ones marked. */
export function ChangeList({ changes, empty }: { changes: ConfigChange[]; empty: string }) {
  const { t } = usePluginT();
  if (changes.length === 0) return <p className="text-sm text-muted-foreground">{empty}</p>;
  return (
    <ul className="divide-y rounded-md border text-sm">
      {changes.map((c, i) => (
        <li key={i} className="flex items-center gap-2 px-3 py-1.5">
          <span className={`w-4 font-mono ${c.op === 'delete' ? 'text-destructive' : c.op === 'create' ? 'text-emerald-600' : 'text-muted-foreground'}`}>
            {OP_MARK[c.op]}
          </span>
          <span className="text-muted-foreground">{t(`kinds.${c.resourceType}`, { defaultValue: c.resourceType })}</span>
          <span className="font-mono">{c.path}</span>
          {c.destructive ? <AlertTriangle className="ml-auto size-3.5 text-amber-600" aria-label={t('publish.destructiveOne')} /> : null}
        </li>
      ))}
    </ul>
  );
}
