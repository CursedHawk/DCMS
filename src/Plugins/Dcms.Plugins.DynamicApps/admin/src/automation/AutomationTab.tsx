import { useMemo, useState } from 'react';
import { Play, RefreshCw, RotateCcw } from 'lucide-react';
import {
  Badge,
  Button,
  CenteredSpinner,
  DataTable,
  Sheet,
  SheetBody,
  SheetContent,
  SheetHeader,
  SheetTitle,
  toast,
  toastApiError,
  type Column,
} from '@dcms/ui';
import { instancePath, useCan, usePluginApi, usePluginT } from '@dcms/plugin-ui';
import { ConfirmDialog } from '../ConfirmDialog';
import { useQueryClient } from '@tanstack/react-query';
import {
  rootKey,
  useActions,
  useApplyChanges,
  useRun,
  useRuns,
  type AppConfig,
  type FlowDef,
  type FlowRunSummary,
} from '../api';
import { Item, Muted, Section } from '../model/ModelTab';
import { FlowDialog } from './FlowDialog';
import { SelectField } from '../model/dialogs';

const STATUS_VARIANT: Record<FlowRunSummary['status'], 'default' | 'secondary' | 'outline' | 'destructive'> = {
  pending: 'outline',
  running: 'secondary',
  succeeded: 'default',
  skipped: 'outline',
  failed: 'destructive',
  terminated: 'destructive',
};

/** Flows (configuration), the actions they can call, and what they did (run history). */
export function AutomationTab({ slug, config, editable, onSelect }: {
  slug: string;
  config: AppConfig | null;
  editable: boolean;
  onSelect: (id: string | null) => void;
}) {
  const { t, i18n } = usePluginT();
  const api = usePluginApi();
  const qc = useQueryClient();
  const apply = useApplyChanges(slug);
  const mayRun = useCan('flows-run');
  const mayReadRuns = useCan('data-read');
  const actions = useActions(slug);
  const [editing, setEditing] = useState<{ flow?: FlowDef } | null>(null);
  const [deleting, setDeleting] = useState<FlowDef | null>(null);
  const [flowFilter, setFlowFilter] = useState('');
  const [statusFilter, setStatusFilter] = useState('');
  const [openRun, setOpenRun] = useState<string | null>(null);
  const runs = useRuns(slug, flowFilter || null, statusFilter || null);
  const flows = config?.flows ?? [];
  const tableName = (id?: string) => config?.tables.find((x) => x.id === id)?.apiName;
  const when = (iso?: string) => (iso ? new Date(iso).toLocaleString(i18n.language) : '—');

  const start = async (flow: FlowDef) => {
    try {
      await api.post(instancePath(slug, `/_automation/flows/${flow.apiName}/run`), { input: {} });
      toast.success(t('automation.started', { flow: flow.displayName }));
      await qc.invalidateQueries({ queryKey: [rootKey(slug), 'runs'] });
    } catch (error) {
      toastApiError(error, t);
    }
  };

  const runColumns = useMemo<Column<FlowRunSummary>[]>(() => [
    { id: 'flow', header: t('automation.flow'), primary: true, cell: (r) => <span className="font-mono">{r.flow}</span> },
    { id: 'status', header: t('automation.status'), cell: (r) => <Badge tone={STATUS_VARIANT[r.status]}>{t(`runStatus.${r.status}`)}</Badge> },
    { id: 'trigger', header: t('automation.trigger'), cell: (r) => r.trigger },
    { id: 'when', header: t('automation.when'), cell: (r) => when(r.createdAt) },
    { id: 'depth', header: t('automation.depth'), align: 'right', cell: (r) => r.depth },
    { id: 'error', header: t('automation.error'), cell: (r) => <span className="line-clamp-1 text-xs text-destructive">{r.error}</span> },
  ], [t, i18n.language]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div className="space-y-6 pt-2">
      <Section title={t('automation.flows')} onAdd={editable && config ? () => setEditing({}) : undefined}>
        {flows.length === 0 ? <Muted>{t('automation.noFlows')}</Muted> : flows.map((flow) => (
          <div key={flow.id} className="flex items-center gap-1" onFocus={() => onSelect(flow.id)}>
            <div className="min-w-0 flex-1">
              <Item
                title={`${flow.displayName}${flow.enabled ? '' : ` · ${t('automation.disabled')}`}`}
                detail={`${flow.trigger.event}${tableName(flow.trigger.tableId) ? ` · ${tableName(flow.trigger.tableId)}` : ''}${flow.trigger.everyMinutes ? ` · ${t('automation.every', { minutes: flow.trigger.everyMinutes })}` : ''} · ${t('automation.steps', { count: flow.steps.length })}`}
                onEdit={editable ? () => { onSelect(flow.id); setEditing({ flow }); } : undefined}
                onDelete={editable ? () => setDeleting(flow) : undefined}
              />
            </div>
            {mayRun && flow.trigger.event === 'manual' && flow.enabled ? (
              <Button variant="ghost" size="icon" aria-label={t('automation.runNow')} onClick={() => void start(flow)} className="mr-2">
                <Play className="size-4" />
              </Button>
            ) : null}
          </div>
        ))}
      </Section>

      {mayReadRuns ? (
        <section className="space-y-2">
          <div className="flex flex-wrap items-end gap-3">
            <h3 className="mr-auto text-sm font-semibold">{t('automation.runs')}</h3>
            <Button variant="outline" size="sm" disabled={runs.isFetching} onClick={() => void runs.refetch()}>
              <RefreshCw className="size-4" /> {t('automation.refresh')}
            </Button>
            <div className="w-48">
              <SelectField id="run-flow" label={t('automation.flow')} value={flowFilter} onChange={(v) => setFlowFilter(v === '*' ? '' : v)}
                options={[{ value: '*', label: t('automation.allFlows') }, ...flows.map((f) => ({ value: f.apiName, label: f.displayName }))]} />
            </div>
            <div className="w-40">
              <SelectField id="run-status" label={t('automation.status')} value={statusFilter} onChange={(v) => setStatusFilter(v === '*' ? '' : v)}
                options={[{ value: '*', label: t('automation.allStatuses') },
                  ...(['pending', 'running', 'succeeded', 'skipped', 'failed', 'terminated'] as const).map((s) => ({ value: s, label: t(`runStatus.${s}`) }))]} />
            </div>
          </div>
          <DataTable rows={runs.data?.items ?? []} columns={runColumns} rowKey={(r) => r.id} isLoading={runs.isLoading}
            empty={t('automation.noRuns')} onRowClick={(r) => setOpenRun(r.id)} />
        </section>
      ) : null}

      <details className="rounded-md border p-3">
        <summary className="cursor-pointer text-sm font-semibold">{t('automation.actions')}</summary>
        <ul className="mt-3 space-y-2 text-sm">
          {(actions.data ?? []).map((a) => (
            <li key={a.key}>
              <span className="font-mono">{a.key}</span>
              {a.risk === 'dangerous' ? <Badge tone="destructive" className="ml-2">{t('automation.dangerous')}</Badge> : null}
              <p className="text-muted-foreground">{a.description}</p>
            </li>
          ))}
        </ul>
      </details>

      {editing && config ? (
        <FlowDialog config={config} flow={editing.flow} actions={actions.data ?? []} pending={apply.isPending}
          onCancel={() => setEditing(null)}
          onSave={(operations) => apply.mutate({ operations }, { onSuccess: () => setEditing(null) })} />
      ) : null}
      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t('model.deleteTitle', { name: deleting?.apiName })}
        description={t('automation.deleteHint')}
        confirmLabel={t('actions.delete')}
        pending={apply.isPending}
        onConfirm={() => deleting && apply.mutate({ operations: [{ op: 'delete', type: 'flow', target: deleting.id }] }, { onSuccess: () => setDeleting(null) })}
      />
      <RunSheet slug={slug} id={openRun} onClose={() => setOpenRun(null)} onOpen={setOpenRun} mayRetry={mayRun} />
    </div>
  );
}

/** One run: its trigger, every step's input, output and error, and the runs the same action set off. */
function RunSheet({ slug, id, onClose, onOpen, mayRetry }: {
  slug: string;
  id: string | null;
  onClose: () => void;
  onOpen: (id: string) => void;
  mayRetry: boolean;
}) {
  const { t, i18n } = usePluginT();
  const api = usePluginApi();
  const qc = useQueryClient();
  const run = useRun(slug, id);
  const detail = run.data;
  const retry = async () => {
    try {
      await api.post(instancePath(slug, `/_automation/runs/${id}/retry`));
      toast.success(t('automation.retried'));
      await qc.invalidateQueries({ queryKey: [rootKey(slug)] });
    } catch (error) {
      toastApiError(error, t);
    }
  };
  const duration = (from?: string, to?: string) =>
    from && to ? `${Math.max(0, new Date(to).getTime() - new Date(from).getTime())} ms` : '';

  return (
    <Sheet open={id !== null} onOpenChange={(open) => !open && onClose()}>
      <SheetContent className="w-full max-w-2xl sm:max-w-2xl">
        <SheetHeader>
          <SheetTitle>{detail ? `${detail.run.flow} · ${t(`runStatus.${detail.run.status}`)}` : t('automation.run')}</SheetTitle>
        </SheetHeader>
        <SheetBody className="space-y-4 overflow-y-auto">
          {!detail ? <CenteredSpinner /> : (
            <>
              <dl className="grid grid-cols-[8rem_1fr] gap-x-3 gap-y-1 text-sm">
                <dt className="text-muted-foreground">{t('automation.trigger')}</dt><dd>{detail.run.trigger}</dd>
                <dt className="text-muted-foreground">{t('automation.revision')}</dt><dd>{detail.run.revision}</dd>
                <dt className="text-muted-foreground">{t('automation.attempts')}</dt><dd>{detail.run.attempts}</dd>
                <dt className="text-muted-foreground">{t('automation.writes')}</dt><dd>{detail.run.writes}</dd>
                <dt className="text-muted-foreground">{t('automation.when')}</dt>
                <dd>{new Date(detail.run.createdAt).toLocaleString(i18n.language)} {duration(detail.run.startedAt, detail.run.finishedAt)}</dd>
                {detail.run.error ? <><dt className="text-muted-foreground">{t('automation.error')}</dt><dd className="text-destructive">{detail.run.error}</dd></> : null}
              </dl>
              {mayRetry && (detail.run.status === 'failed' || detail.run.status === 'terminated') ? (
                <Button size="sm" variant="outline" onClick={() => void retry()}><RotateCcw className="size-4" /> {t('automation.retry')}</Button>
              ) : null}

              <h4 className="text-sm font-semibold">{t('automation.stepsTitle')}</h4>
              <ol className="space-y-2">
                {detail.steps.map((s, i) => (
                  <li key={i} className="rounded-md border p-2 text-sm">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-mono font-medium">{s.stepId}</span>
                      <span className="font-mono text-xs text-muted-foreground">{s.action}</span>
                      <Badge tone={STATUS_VARIANT[s.status]}>{t(`runStatus.${s.status}`)}</Badge>
                      {s.attempt > 1 ? <span className="text-xs text-muted-foreground">{t('automation.attempt', { n: s.attempt })}</span> : null}
                      <span className="ml-auto text-xs text-muted-foreground">{duration(s.startedAt, s.finishedAt)}</span>
                    </div>
                    {s.error ? <p className="mt-1 text-destructive">{s.error}</p> : null}
                    <Json label={t('automation.input')} value={s.input} />
                    <Json label={t('automation.output')} value={s.output} />
                  </li>
                ))}
              </ol>

              <Json label={t('automation.event')} value={detail.triggerEvent} />

              {detail.chain.length > 1 ? (
                <>
                  <h4 className="text-sm font-semibold">{t('automation.chain')}</h4>
                  <ol className="space-y-1 text-sm">
                    {detail.chain.map((c) => (
                      <li key={c.id}>
                        <button type="button" className={`text-left hover:underline ${c.id === detail.run.id ? 'font-semibold' : ''}`}
                          style={{ paddingLeft: `${c.depth}rem` }} onClick={() => onOpen(c.id)}>
                          {c.flow} · {t(`runStatus.${c.status}`)}
                        </button>
                      </li>
                    ))}
                  </ol>
                </>
              ) : null}
            </>
          )}
        </SheetBody>
      </SheetContent>
    </Sheet>
  );
}

function Json({ label, value }: { label: string; value: unknown }) {
  if (value === null || value === undefined) return null;
  return (
    <details className="mt-1">
      <summary className="cursor-pointer text-xs text-muted-foreground">{label}</summary>
      <pre className="mt-1 max-h-64 overflow-auto rounded bg-muted p-2 text-xs">{JSON.stringify(value, null, 2)}</pre>
    </details>
  );
}
