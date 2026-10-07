import { useMemo, useState } from 'react';
import { Badge, Button, DataTable, toast, type Column } from '@dcms/ui';
import { instancePath, usePluginApi, usePluginT } from '@dcms/plugin-ui';
import { ConfirmDialog } from './ConfirmDialog';
import { refusedOf, useModelAction, useRevisionChanges, useRevisions, type PublishResult, type RevisionInfo } from './api';
import { ChangeList, Issues } from './PublishDialog';

const LIVE_ONCE = new Set<RevisionInfo['status']>(['published', 'superseded', 'rolledBack']);

/** Every revision, what each changed, and rolling back to an earlier published one (as a new revision). */
export function RevisionsTab({ slug, mayRollback }: { slug: string; mayRollback: boolean }) {
  const { t, i18n } = usePluginT();
  const api = usePluginApi();
  const revisions = useRevisions(slug);
  const [open, setOpen] = useState<number | null>(null);
  const [rollbackTo, setRollbackTo] = useState<RevisionInfo | null>(null);
  const [refused, setRefused] = useState<PublishResult | null>(null);
  const changes = useRevisionChanges(slug, open);
  const rollback = useModelAction(slug, (to: number) =>
    api.post<PublishResult>(instancePath(slug, '/_model/rollback'), { toRevision: to }));

  const columns = useMemo<Column<RevisionInfo>[]>(() => [
    { id: 'number', header: '#', width: '4rem', primary: true, cell: (r) => <span className="font-mono">r{r.number}</span> },
    {
      id: 'status',
      header: t('revisions.status'),
      cell: (r) => <Badge tone={r.status === 'published' ? 'default' : r.status === 'draft' ? 'secondary' : 'outline'}>{t(`revisionStatus.${r.status}`)}</Badge>,
    },
    { id: 'source', header: t('revisions.source'), cell: (r) => t(`sources.${r.source}`) },
    { id: 'description', header: t('form.description'), cell: (r) => <span className="line-clamp-1">{r.description}</span> },
    { id: 'when', header: t('revisions.when'), cell: (r) => new Date(r.publishedAt ?? r.updatedAt).toLocaleString(i18n.language) },
    {
      id: 'actions',
      header: '',
      align: 'right',
      hideOnCard: true,
      cell: (r) => mayRollback && LIVE_ONCE.has(r.status) && r.status !== 'published' ? (
        <Button variant="outline" size="sm" onClick={(e) => { e.stopPropagation(); setRollbackTo(r); }}>{t('revisions.rollback')}</Button>
      ) : null,
    },
  ], [t, i18n.language, mayRollback]);

  return (
    <div className="space-y-4 pt-2">
      <DataTable rows={revisions.data?.items ?? []} columns={columns} rowKey={(r) => r.id} isLoading={revisions.isLoading}
        empty={t('revisions.empty')} onRowClick={(r) => setOpen(r.number === open ? null : r.number)} />
      {open !== null ? (
        <section className="space-y-2">
          <h3 className="text-sm font-semibold">{t('revisions.changesOf', { number: open })}</h3>
          <ChangeList changes={changes.data ?? []} empty={t('publish.noChanges')} />
        </section>
      ) : null}
      {refused ? <Issues issues={refused.issues} /> : null}
      <ConfirmDialog
        open={rollbackTo !== null}
        onOpenChange={(o) => !o && setRollbackTo(null)}
        title={t('revisions.rollbackTitle', { number: rollbackTo?.number })}
        description={t('revisions.rollbackHint')}
        confirmLabel={t('revisions.rollback')}
        pending={rollback.isPending}
        onConfirm={() => rollbackTo && rollback.mutate(rollbackTo.number, {
          onSuccess: (result) => {
            setRollbackTo(null);
            if (result.published) {
              setRefused(null);
              toast.success(t('revisions.rolledBack', { number: result.revision?.number }));
            } else {
              setRefused(result);
            }
          },
          onError: (error) => {
            const result = refusedOf(error);
            if (result) {
              setRollbackTo(null);
              setRefused(result);
              toast.error(t('publish.refused'));
            }
          },
        })}
      />
    </div>
  );
}
