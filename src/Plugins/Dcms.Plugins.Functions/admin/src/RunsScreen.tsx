import { useMemo, useState } from 'react';
import { Badge, Button, DataTable, Input, LoadFailed, type Column } from '@dcms/ui';
import { usePluginT, type PluginScreenProps } from '@dcms/plugin-ui';
import { useRun, useRuns, type RunStatus, type RunSummary } from './api';

const TONE: Record<RunStatus, 'default' | 'secondary' | 'success' | 'warning' | 'destructive' | 'outline'> = {
  Pending: 'secondary',
  Running: 'default',
  Succeeded: 'success',
  Failed: 'destructive',
  Terminated: 'warning',
};
const STATUSES: RunStatus[] = ['Pending', 'Running', 'Succeeded', 'Failed', 'Terminated'];

/** Every run, newest first: what started it, what it got, what it gave back and what it logged. */
export function RunsScreen({ instance }: PluginScreenProps) {
  const slug = instance!.slug;
  const { t, i18n } = usePluginT();
  const [fn, setFn] = useState('');
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const runs = useRuns(slug, { function: fn.trim() || undefined, status: status || undefined, page });
  const [open, setOpen] = useState<string | null>(null);
  const run = useRun(slug, open);

  const columns = useMemo<Column<RunSummary>[]>(() => [
    { id: 'function', header: t('runs.function'), primary: true, cell: (r) => <span className="font-mono">{r.function}</span> },
    { id: 'status', header: t('runs.status'), cell: (r) => <Badge tone={TONE[r.status]}>{t(`runStatus.${r.status}`)}</Badge> },
    { id: 'trigger', header: t('runs.trigger'), cell: (r) => t(`runTrigger.${r.trigger}`, { defaultValue: r.trigger }) },
    { id: 'duration', header: t('runs.duration'), align: 'right', cell: (r) => `${r.durationMs} ms` },
    { id: 'when', header: t('runs.when'), cell: (r) => new Date(r.createdAt).toLocaleString(i18n.language) },
  ], [t, i18n.language]);

  return (
    <div className="space-y-4 pt-2">
      <div className="flex flex-wrap items-center gap-2">
        <Input className="w-56" placeholder={t('runs.filterFunction')} value={fn} onChange={(e) => { setFn(e.target.value); setPage(1); }} />
        <select className="h-9 rounded-md border bg-background px-2 text-sm" value={status} aria-label={t('runs.status')}
          onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
          <option value="">{t('runs.anyStatus')}</option>
          {STATUSES.map((s) => <option key={s} value={s}>{t(`runStatus.${s}`)}</option>)}
        </select>
      </div>
      <DataTable rows={runs.data?.items ?? []} columns={columns} rowKey={(r) => r.id} isLoading={runs.isLoading}
        error={runs.isError ? <LoadFailed title={t('common.loadFailed')} retryLabel={t('common.retry')} onRetry={() => void runs.refetch()} /> : undefined}
        empty={t('runs.empty')} onRowClick={(r) => setOpen(r.id === open ? null : r.id)} />
      <div className="flex justify-end gap-2">
        <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>{t('runs.newer')}</Button>
        <Button variant="outline" size="sm" disabled={!runs.data?.more} onClick={() => setPage((p) => p + 1)}>{t('runs.older')}</Button>
      </div>
      {open && run.data ? (
        <section className="space-y-3 rounded-lg border p-4">
          <h3 className="text-sm font-semibold">{t('runs.detail', { function: run.data.run.function, deployment: run.data.run.deployment })}</h3>
          {run.data.run.error ? <p className="text-sm text-destructive">{run.data.run.error}</p> : null}
          <Block title={t('runs.input')} value={run.data.input} />
          <Block title={t('runs.result')} value={run.data.result} />
          <Block title={t('runs.triggerDetail')} value={run.data.trigger} />
          <div className="space-y-1">
            <p className="text-xs font-medium text-muted-foreground">{t('runs.logs')}</p>
            <pre className="max-h-80 overflow-auto rounded-md bg-muted p-3 text-xs">{run.data.logs || t('runs.noLogs')}</pre>
          </div>
          <p className="text-xs text-muted-foreground">
            {t('runs.meta', { attempts: run.data.run.attempts, calls: run.data.calls, depth: run.data.depth })}
          </p>
        </section>
      ) : null}
    </div>
  );
}

function Block({ title, value }: { title: string; value: unknown }) {
  if (value === undefined || value === null) return null;
  return (
    <div className="space-y-1">
      <p className="text-xs font-medium text-muted-foreground">{title}</p>
      <pre className="max-h-60 overflow-auto rounded-md bg-muted p-3 text-xs">{JSON.stringify(value, null, 2)}</pre>
    </div>
  );
}
