import { Link, useSearch } from '@tanstack/react-router';
import { ChevronDown, ChevronRight, Loader, TriangleAlert, X } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import {
  Button, CenteredSpinner, DataTable, EmptyState, JobDetails, JobRow, StatCard, Tabs, TabsList, TabsTrigger, cn,
  type Column, type JobDetailsLabels,
} from '@dcms/ui';
import { can, Perm, useMe } from '../../lib/permissions';
import {
  type Job, type JobListFilter, type JobScope, type QueueDepth, type TenantJobSummary,
  useCancelJob, useJob, useJobOverview, useJobs, useRetryJob,
} from './api';
import { jobDetail, jobOrigin, jobTitle, jobTrigger, STATUS_LABEL } from './render';

const DETAIL_LABELS: JobDetailsLabels = {
  rel: { source: 'Source', config: 'Configuration', run: 'Run details', result: 'Result' },
  open: 'Open',
  trigger: 'Trigger',
  queued: 'Queued',
  started: 'Started',
  finished: 'Finished',
  duration: 'Duration',
  attempts: 'Attempts',
  attemptsValue: (attempt, max) => `${attempt} of ${max}`,
  progress: 'Progress',
  error: 'Error',
  outcome: 'Outcome',
  id: 'Job ID',
};

const FILTERS: { id: JobListFilter; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'active', label: 'In progress' },
  { id: 'failed', label: 'Failed' },
];

/**
 * Every background job on the platform (ADR 0024): how much is running and waiting per tenant,
 * how deep each work queue is, and the jobs themselves — narrowed to one tenant, or to the
 * platform's own work with no tenant at all.
 */
export function JobsPage() {
  const { job: focused } = useSearch({ strict: false }) as { job?: string };
  const me = useMe(true);
  const mayManage = can(me.data, Perm.JobsManage);
  const overview = useJobOverview();
  const [filter, setFilter] = useState<JobListFilter>('all');
  const [scope, setScope] = useState<JobScope & { label?: string }>({});
  const { label: scopeLabel, ...query } = scope as JobScope & { label?: string };
  // ponytail: the newest 100 in scope; a cursor-driven "load more" when history matters.
  const jobs = useJobs(filter, 100, query as JobScope);

  const tenants = overview.data?.tenants ?? [];
  const sum = (pick: (t: TenantJobSummary) => number) => tenants.reduce((a, t) => a + pick(t), 0);

  return (
    <div className="mx-auto w-full max-w-6xl px-4 py-8 sm:px-6">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Background jobs</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Imports, conversions, deployments and plugin work across every tenant, and the platform’s own.
        </p>
      </header>

      <div className="mb-8 grid grid-cols-1 gap-4 sm:grid-cols-3">
        <StatCard label="Running" value={sum((t) => t.running)} isLoading={overview.isLoading} />
        <StatCard label="Queued" value={sum((t) => t.queued)} isLoading={overview.isLoading} />
        <StatCard label="Failed, last 24 hours" value={sum((t) => t.failedLast24h)} isLoading={overview.isLoading} />
      </div>

      <div className="mb-8 grid grid-cols-1 gap-6 lg:grid-cols-2">
        <section>
          <h2 className="mb-2 text-sm font-semibold">Work queues</h2>
          <DataTable<QueueDepth>
            rows={overview.data?.queues}
            isLoading={overview.isLoading}
            rowKey={(q) => `${q.stream}/${q.consumer}`}
            columns={QUEUE_COLUMNS}
            empty="No work queues reported."
          />
        </section>
        <section>
          <h2 className="mb-2 text-sm font-semibold">By tenant</h2>
          <DataTable<TenantJobSummary>
            rows={tenants}
            isLoading={overview.isLoading}
            rowKey={(t) => t.tenantId ?? 'platform'}
            columns={TENANT_COLUMNS}
            empty="No tenant has work running, queued or recently failed."
            onRowClick={(t) =>
              setScope(
                t.tenantId
                  ? { tenantId: t.tenantId, label: t.tenantSlug ?? t.tenantId }
                  : { platformOnly: true, label: 'Platform' },
              )
            }
          />
        </section>
      </div>

      <section>
        <div className="mb-3 flex flex-wrap items-center gap-3">
          <h2 className="text-sm font-semibold">Jobs</h2>
          <Tabs value={filter} onValueChange={(v) => setFilter(v as JobListFilter)}>
            <TabsList>
              {FILTERS.map((f) => (
                <TabsTrigger key={f.id} value={f.id}>
                  {f.label}
                </TabsTrigger>
              ))}
            </TabsList>
          </Tabs>
          {scopeLabel ? (
            <Button variant="outline" size="sm" onClick={() => setScope({})}>
              {scopeLabel} <X className="h-3.5 w-3.5" aria-label="Show every tenant" />
            </Button>
          ) : (
            <Button variant="ghost" size="sm" onClick={() => setScope({ platformOnly: true, label: 'Platform' })}>
              Platform only
            </Button>
          )}
        </div>

        {jobs.isLoading ? (
          <CenteredSpinner />
        ) : (jobs.data?.items.length ?? 0) === 0 ? (
          <EmptyState icon={Loader} title="No jobs" description="Nothing in this view." />
        ) : (
          <div className="overflow-hidden rounded-md border border-border bg-card">
            {jobs.data!.items.map((job) => (
              <JobEntry key={job.id} job={job} initiallyOpen={job.id === focused} mayManage={mayManage} />
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

const QUEUE_COLUMNS: Column<QueueDepth>[] = [
  { id: 'stream', header: 'Stream', cell: (q) => <span className="font-mono text-xs">{q.stream}</span>, primary: true },
  {
    id: 'consumer',
    header: 'Drained by',
    cell: (q) =>
      q.consumer ? (
        <span className="font-mono text-xs">{q.consumer}</span>
      ) : (
        <span className={cn('inline-flex items-center gap-1 text-xs', q.pending > 0 ? 'text-destructive' : 'text-muted-foreground')}>
          <TriangleAlert className="h-3.5 w-3.5" aria-hidden /> nobody
        </span>
      ),
  },
  { id: 'pending', header: 'Waiting', cell: (q) => q.pending, sortValue: (q) => q.pending, align: 'right' },
  { id: 'ack', header: 'In flight', cell: (q) => q.ackPending, sortValue: (q) => q.ackPending, align: 'right' },
];

const TENANT_COLUMNS: Column<TenantJobSummary>[] = [
  {
    id: 'tenant',
    header: 'Tenant',
    primary: true,
    cell: (t) =>
      t.tenantId ? (
        <Link to="/tenants/$tenantId" params={{ tenantId: t.tenantId }} className="hover:underline" onClick={(e) => e.stopPropagation()}>
          {t.tenantName ?? t.tenantSlug ?? t.tenantId}
        </Link>
      ) : (
        <span className="text-muted-foreground">Platform</span>
      ),
  },
  { id: 'running', header: 'Running', cell: (t) => t.running, sortValue: (t) => t.running, align: 'right' },
  { id: 'queued', header: 'Queued', cell: (t) => t.queued, sortValue: (t) => t.queued, align: 'right' },
  {
    id: 'failed',
    header: 'Failed 24h',
    cell: (t) => <span className={cn(t.failedLast24h > 0 && 'text-destructive')}>{t.failedLast24h}</span>,
    sortValue: (t) => t.failedLast24h,
    align: 'right',
  },
];

function JobEntry({ job, initiallyOpen, mayManage }: { job: Job; initiallyOpen: boolean; mayManage: boolean }) {
  const isGroup = job.lane === 'group';
  const [open, setOpen] = useState(initiallyOpen);
  const detail = useJob(open && isGroup ? job.id : undefined);

  return (
    <div className={cn('border-b border-border last:border-b-0', initiallyOpen && 'bg-primary/[0.03]')}>
      <div className="flex items-start">
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
          aria-label="Details"
          className="mt-2.5 pl-2 text-muted-foreground hover:text-foreground"
        >
          {open ? <ChevronDown className="h-4 w-4" aria-hidden /> : <ChevronRight className="h-4 w-4" aria-hidden />}
        </button>
        <div className="min-w-0 flex-1">
          <JobLine job={job} mayManage={mayManage} withTenant />
        </div>
      </div>
      {open ? (
        <div className="ml-6">
          <JobDetails
            job={job}
            labels={DETAIL_LABELS}
            trigger={jobTrigger(job)}
            stage={job.status === 'Running' ? jobDetail(job) : undefined}
            error={job.status === 'Failed' ? jobDetail(job) : undefined}
            // Paths in the tenant's own console: named, with the path for whoever goes there.
            renderLink={(link, label) => (
              <span>
                {label} <span className="font-mono text-muted-foreground">{link.path}</span>
              </span>
            )}
          />
          {isGroup ? (
            <div className="border-l border-border bg-muted/20">
              <p className="px-3 pt-2 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Items</p>
              {detail.isLoading ? (
                <CenteredSpinner />
              ) : (
                detail.data?.children.map((c) => <JobLine key={c.id} job={c} mayManage={mayManage} withTenant={false} />)
              )}
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function JobLine({ job, mayManage, withTenant }: { job: Job; mayManage: boolean; withTenant: boolean }) {
  const cancel = useCancelJob();
  const retry = useRetryJob();
  return (
    <JobRow
      job={job}
      text={{ title: jobTitle(job, withTenant), detail: jobDetail(job), origin: jobOrigin(job) }}
      labels={{ cancel: 'Cancel', retry: 'Retry', status: (s) => STATUS_LABEL[s] }}
      onCancel={
        mayManage
          ? () =>
              cancel.mutate(job.id, {
                onSuccess: (r) => toast(r.status === 'canceled' ? 'Job canceled.' : 'Stopping the job…'),
                onError: () => toast.error('This job can no longer be canceled.'),
              })
          : undefined
      }
      onRetry={
        mayManage
          ? () =>
              retry.mutate(job.id, {
                onSuccess: () => toast('Job queued again.'),
                onError: () => toast.error("This job can't be retried from here."),
              })
          : undefined
      }
    />
  );
}
