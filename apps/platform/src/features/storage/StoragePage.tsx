import { useState } from 'react';
import { AlertTriangle, ShieldCheck } from 'lucide-react';
import { Button, CenteredSpinner, ConfirmDeleteDialog, EmptyState, Input, toastApiError } from '@dcms/ui';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Rail } from '../../components/Rail';
import { bytes, count, date } from '../../lib/format';
import { can, useMe, Perm } from '../../lib/permissions';
import {
  useCancelPurge, usePendingPurges, usePruneAnalytics, usePurgeLoki, useStores,
  useTruncateContainerLog,
} from './api';

/** Which destructive action a confirmation dialog is currently standing in front of. */
type Pending =
  | { kind: 'loki'; selector: string; days: number }
  | { kind: 'analytics'; days: number }
  | { kind: 'docker'; container: string };

/**
 * What the telemetry stores are holding, and what can be deleted from them.
 *
 * <p>The three stores behave differently and the page says so rather than presenting one
 * uniform Purge button that fails on two of them: Loki deletes by selector with a two-hour
 * grace period, Prometheus deletes series and needs its tombstones cleaned, and Tempo has no
 * delete API at all.</p>
 */
export function StoragePage() {
  const { t } = useTranslation();
  const me = useMe(true);
  const stores = useStores();
  const pending = usePendingPurges();
  const purge = usePurgeLoki();
  const cancel = useCancelPurge();
  const prune = usePruneAnalytics();
  const truncate = useTruncateContainerLog();

  const mayPurge = can(me.data, Perm.LogsPurge);
  const onError = (e: unknown) => toastApiError(e, t);

  const [selector, setSelector] = useState('{service="content-api"}');
  const [days, setDays] = useState('30');
  const [pruneDays, setPruneDays] = useState('90');
  const [container, setContainer] = useState('');
  const [confirming, setConfirming] = useState<Pending | null>(null);

  if (stores.isLoading) return <CenteredSpinner />;

  const data = stores.data;
  const live = pending.data?.filter((p) => p.status !== 'processed') ?? [];

  return (
    <div className="mx-auto w-full max-w-4xl px-6 py-8">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Storage</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          The telemetry stores against the budgets they were sized to, and what can be deleted
          from each.
        </p>
      </header>

      {/* Stated once, at the top, and not as a warning banner: it is a property of the system
          rather than a problem with it. */}
      <div className="mb-8 flex items-start gap-3 rounded-md border border-border bg-card p-4">
        <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-[hsl(var(--success))]" aria-hidden />
        <p className="text-sm text-muted-foreground">
          The audit log cannot be deleted from here, and not because a button is hidden: this
          console connects to Postgres as a role with no grant on the audit schema. Audit and
          security log streams are refused for the same reason — they are kept 90 days as an
          integrity control.
        </p>
      </div>

      {data?.prometheusUnreachable && (
        <Problem
          title="Prometheus is not answering"
          detail="Store sizes come from the metrics it holds, so this page has nothing to show until it is back."
        />
      )}

      <Section title="Stores">
        {data && data.stores.length === 0 ? (
          <EmptyState
            title={
              data.awaitingStoreMetrics
                ? 'No store sizes reported yet'
                : 'No store sizes available'
            }
            description={
              data.awaitingStoreMetrics
                ? 'Prometheus is answering, but the store-usage sidecar has not written its first sample. It reports every five minutes.'
                : 'Store sizes come from Prometheus, which is not answering.'
            }
          />
        ) : (
        <div className="flex flex-col gap-5">
          {data?.stores.map((s) => (
            <div key={s.store}>
              <Rail
                label={s.store}
                used={s.usedBytes}
                limit={s.budgetBytes || s.usedBytes}
                window={
                  s.retentionSeconds > 0
                    ? `${Math.round(s.retentionSeconds / 86_400)}-day retention`
                    : undefined
                }
                hint={
                  s.projectedBytes > 0
                    ? `projected ${bytes(s.projectedBytes)} at the end of the window`
                    : undefined
                }
              />
              {s.purgeNote && (
                <p className="mt-1 pl-0 text-xs text-muted-foreground sm:pl-[calc(9rem+1rem)]">
                  {s.purgeNote}
                </p>
              )}
            </div>
          ))}
        </div>
        )}
      </Section>

      {live.length > 0 && (
        <Section title="Queued deletions">
          <p className="mb-3 text-sm text-muted-foreground">
            Loki applies these after a two-hour delay. Until then they can be cancelled.
          </p>
          <ul className="flex flex-col gap-2">
            {live.map((p) => (
              <li
                key={p.requestId}
                className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-border bg-card px-3 py-2"
              >
                <div className="min-w-0">
                  <p className="truncate font-mono text-xs">{p.query}</p>
                  <p className="text-xs text-muted-foreground">
                    {date(p.startTime)} to {date(p.endTime)} · {p.status}
                  </p>
                </div>
                {mayPurge && (
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={cancel.isPending}
                    onClick={() =>
                      cancel.mutate(p.requestId, {
                        onSuccess: () => toast.success('Deletion cancelled'),
                        onError,
                      })
                    }
                  >
                    Cancel
                  </Button>
                )}
              </li>
            ))}
          </ul>
        </Section>
      )}

      {mayPurge && (
        <>
          <Section title="Delete log lines">
            <p className="mb-3 text-sm text-muted-foreground">
              Names a stream and a window. Audit and security streams are excluded by the server,
              and it tells you the selector it actually used.
            </p>
            <div className="flex flex-wrap items-end gap-3">
              <label className="flex min-w-[18rem] flex-1 flex-col gap-1">
                <span className="text-sm">Stream selector</span>
                <Input
                  value={selector}
                  onChange={(e) => setSelector(e.target.value)}
                  className="font-mono text-xs"
                  placeholder='{service="content-api"}'
                />
              </label>
              <label className="flex w-32 flex-col gap-1">
                <span className="text-sm">Older than</span>
                <Input
                  type="number"
                  min={1}
                  value={days}
                  onChange={(e) => setDays(e.target.value)}
                />
              </label>
              <Button
                variant="destructive"
                disabled={purge.isPending || !selector.trim()}
                onClick={() => {
                  const olderThan = Number(days);
                  if (!Number.isFinite(olderThan) || olderThan < 1) {
                    toast.error('Give a number of days.');
                    return;
                  }
                  setConfirming({ kind: 'loki', selector: selector.trim(), days: olderThan });
                }}
              >
                Delete matching lines
              </Button>
            </div>
          </Section>

          <Section title="Prune visitor analytics">
            <p className="mb-3 text-sm text-muted-foreground">
              Raw visitor events older than the cutoff. Daily rollups are kept forever and are not
              affected — they are what still answers questions about last year.
            </p>
            <div className="flex flex-wrap items-end gap-3">
              <label className="flex w-40 flex-col gap-1">
                <span className="text-sm">Older than (days)</span>
                <Input
                  type="number"
                  min={7}
                  value={pruneDays}
                  onChange={(e) => setPruneDays(e.target.value)}
                />
              </label>
              <Button
                variant="destructive"
                disabled={prune.isPending}
                onClick={() => setConfirming({ kind: 'analytics', days: Number(pruneDays) })}
              >
                Prune events
              </Button>
            </div>
          </Section>

          {data?.dockerLogPurgeAvailable ? (
            <Section title="Container logs">
              <p className="mb-3 text-sm text-muted-foreground">
                Empties one container&rsquo;s Docker log file. Only containers in this compose
                project can be named, and only the log is touched.
              </p>
              <div className="flex flex-wrap items-end gap-3">
                <label className="flex min-w-[16rem] flex-1 flex-col gap-1">
                  <span className="text-sm">Container</span>
                  <Input
                    value={container}
                    onChange={(e) => setContainer(e.target.value)}
                    className="font-mono text-xs"
                    placeholder="dcms-content-api-1"
                  />
                </label>
                <Button
                  variant="destructive"
                  disabled={truncate.isPending || !container.trim()}
                  onClick={() => setConfirming({ kind: 'docker', container: container.trim() })}
                >
                  Truncate log
                </Button>
              </div>
            </Section>
          ) : (
            <Section title="Container logs">
              <p className="text-sm text-muted-foreground">
                Truncating container logs is not enabled on this deployment. It needs the
                log-janitor sidecar, which is off by default because it holds write access to
                Docker&rsquo;s container directory — and container logs are already capped at
                3&nbsp;&times;&nbsp;50&nbsp;MB each, so it is a convenience under disk pressure
                rather than something to turn on by habit.
              </p>
            </Section>
          )}
        </>
      )}

      {/*
        One dialog for all three, because they differ only in what they say. Every one of these
        is irreversible in the sense that matters — Loki's two-hour window aside, nothing here
        comes back — so none of them fires from a single click.
      */}
      {confirming && (
        <ConfirmDeleteDialog
          open
          onOpenChange={(open) => !open && setConfirming(null)}
          title={
            confirming.kind === 'loki'
              ? 'Delete these log lines?'
              : confirming.kind === 'analytics'
                ? 'Prune visitor events?'
                : `Truncate the log for ${confirming.container}?`
          }
          description="Type the word below to confirm."
          confirmationValue={
            confirming.kind === 'docker' ? confirming.container : confirming.kind
          }
          confirmLabel={
            confirming.kind === 'loki'
              ? 'Delete lines'
              : confirming.kind === 'analytics'
                ? 'Prune events'
                : 'Truncate log'
          }
          pending={purge.isPending || prune.isPending || truncate.isPending}
          consequences={
            confirming.kind === 'loki'
              ? [
                  `Every line matching ${confirming.selector} older than ${confirming.days} days.`,
                  'Audit and security streams are excluded by the server.',
                  'Loki applies this after two hours; until then it can be cancelled from this page.',
                ]
              : confirming.kind === 'analytics'
                ? [
                    `Raw visitor events older than ${confirming.days} days, across every tenant.`,
                    'Daily rollups are kept and are not affected.',
                    'This cannot be undone.',
                  ]
                : [
                    `The current and rotated log files for ${confirming.container}.`,
                    'Loki already holds the searchable copy of anything Alloy has shipped.',
                    'This cannot be undone.',
                  ]
          }
          onConfirm={() => {
            if (confirming.kind === 'loki') {
              purge.mutate(
                {
                  selector: confirming.selector,
                  // Loki keeps 30 days, so anything older is already gone; a year back is a
                  // safely wide floor rather than the epoch.
                  start: new Date(Date.now() - 365 * 86_400_000).toISOString(),
                  end: new Date(Date.now() - confirming.days * 86_400_000).toISOString(),
                },
                {
                  onSuccess: (r) => {
                    toast.success(r.message, { description: r.effectiveSelector });
                    setConfirming(null);
                  },
                  onError,
                },
              );
            } else if (confirming.kind === 'analytics') {
              prune.mutate(confirming.days, {
                onSuccess: (r) => {
                  toast.success(`Deleted ${count(r.deleted)} events`, { description: r.note });
                  setConfirming(null);
                },
                onError,
              });
            } else {
              truncate.mutate(confirming.container, {
                onSuccess: (r) => {
                  toast.success(r.message, { description: `Freed ${bytes(r.freedBytes)}` });
                  setConfirming(null);
                },
                onError,
              });
            }
          }}
        />
      )}
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="mb-8 border-t border-border pt-5">
      <h2 className="mb-4 text-sm font-medium text-foreground">{title}</h2>
      {children}
    </section>
  );
}

function Problem({ title, detail }: { title: string; detail: string }) {
  return (
    <div className="mb-6 flex max-w-xl gap-3 rounded-md border border-border bg-card p-4">
      <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
      <div>
        <h2 className="text-sm font-medium">{title}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{detail}</p>
      </div>
    </div>
  );
}
