import { useSearch } from '@tanstack/react-router';
import { ChevronDown, ChevronRight, Loader } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  CenteredSpinner,
  EmptyState,
  JobRow,
  Page,
  PageHeader,
  Tabs,
  TabsList,
  TabsTrigger,
  cn,
} from '@dcms/ui';
import { type Job, type JobListFilter, useCancelJob, useJob, useJobs, useRetryJob } from './api';
import { jobText } from './text';

const FILTERS: JobListFilter[] = ['all', 'active', 'failed'];

/**
 * Every background job in the workspace, newest first, with a group's items under it. The
 * popover is the glance; this is where you go to see what a failed import did with each file.
 * `?job=` (from a popover row) opens that job's items.
 */
export function JobsPage() {
  const { t } = useTranslation();
  const { job: focused } = useSearch({ strict: false }) as { job?: string };
  const [filter, setFilter] = useState<JobListFilter>('all');
  // ponytail: the newest 100; a "load more" over the cursor when somebody needs older history.
  const { data, isLoading } = useJobs(filter, 100);
  const items = data?.items ?? [];

  return (
    <Page>
      <PageHeader title={t('jobs.title')} description={t('jobs.pageDescription')} />

      <Tabs value={filter} onValueChange={(v) => setFilter(v as JobListFilter)} className="mb-4">
        <TabsList>
          {FILTERS.map((f) => (
            <TabsTrigger key={f} value={f}>
              {t(`jobs.filter.${f}`)}
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>

      {isLoading ? (
        <CenteredSpinner />
      ) : items.length === 0 ? (
        <EmptyState icon={Loader} title={t('jobs.empty')} description={t('jobs.emptyDescription')} />
      ) : (
        <div className="overflow-hidden rounded-lg border bg-card">
          {items.map((job) => (
            <JobEntry key={job.id} job={job} initiallyOpen={job.id === focused} />
          ))}
        </div>
      )}
    </Page>
  );
}

function JobEntry({ job, initiallyOpen }: { job: Job; initiallyOpen: boolean }) {
  const { t } = useTranslation();
  const isGroup = job.lane === 'group';
  const [open, setOpen] = useState(initiallyOpen && isGroup);
  const detail = useJob(open ? job.id : undefined);

  return (
    <div className={cn('border-b last:border-b-0', initiallyOpen && 'bg-primary/[0.03]')}>
      <div className="flex items-start">
        {isGroup ? (
          <button
            type="button"
            onClick={() => setOpen((o) => !o)}
            aria-expanded={open}
            aria-label={t('jobs.children')}
            className="mt-2.5 pl-2 text-muted-foreground hover:text-foreground"
          >
            {open ? <ChevronDown className="h-4 w-4" aria-hidden /> : <ChevronRight className="h-4 w-4" aria-hidden />}
          </button>
        ) : (
          <span className="w-6" aria-hidden />
        )}
        <div className="min-w-0 flex-1">
          <JobLine job={job} />
        </div>
      </div>
      {open ? (
        <div className="ml-6 border-l bg-muted/20">
          {detail.isLoading ? (
            <CenteredSpinner />
          ) : (
            detail.data?.children.map((child) => <JobLine key={child.id} job={child} />)
          )}
        </div>
      ) : null}
    </div>
  );
}

function JobLine({ job }: { job: Job }) {
  const { t, i18n } = useTranslation();
  const cancel = useCancelJob();
  const retry = useRetryJob();

  return (
    <JobRow
      job={job}
      text={jobText(t, job)}
      locale={i18n.language}
      labels={{ cancel: t('jobs.cancel'), retry: t('jobs.retry'), status: (s) => t(`jobs.status.${s}`) }}
      onCancel={() =>
        cancel.mutate(job.id, {
          onSuccess: (r) => toast(t(r.status === 'canceled' ? 'jobs.canceled' : 'jobs.cancelRequested')),
          onError: () => toast.error(t('jobs.notCancelable')),
        })
      }
      onRetry={() =>
        retry.mutate(job.id, {
          onSuccess: () => toast(t('jobs.retried')),
          onError: () => toast.error(t('jobs.notRetryable')),
        })
      }
    />
  );
}
