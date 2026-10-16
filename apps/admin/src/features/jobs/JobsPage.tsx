import { Link, useSearch } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { ChevronDown, ChevronRight, Loader } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  CenteredSpinner,
  EmptyState,
  JobDetails,
  JobRow,
  type JobDetailsLabels,
  Page,
  PageHeader,
  Tabs,
  TabsList,
  TabsTrigger,
  cn,
} from '@dcms/ui';
import { type Job, type JobListFilter, useCancelJob, useJob, useJobs, useRetryJob } from './api';
import { linkTarget } from '../notifications/linkPath';
import { jobText, jobTrigger } from './text';

const FILTERS: JobListFilter[] = ['all', 'active', 'failed'];

/**
 * Every background job in the workspace, newest first. The popover is the glance; this is where
 * you go to see where a job came from, what defines it, its run details, and what a failed import
 * did with each file. `?job=` (from a popover row) opens that job.
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
  const { t, i18n } = useTranslation();
  const isGroup = job.lane === 'group';
  const [open, setOpen] = useState(initiallyOpen);
  // A group's items come with its detail; any other job already has everything on its row.
  const detail = useJob(open && isGroup ? job.id : undefined);
  const text = jobText(t, job);

  return (
    <div className={cn('border-b last:border-b-0', initiallyOpen && 'bg-primary/[0.03]')}>
      <div className="flex items-start">
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
          aria-label={t('jobs.detail.toggle')}
          className="mt-2.5 pl-2 text-muted-foreground hover:text-foreground"
        >
          {open ? <ChevronDown className="h-4 w-4" aria-hidden /> : <ChevronRight className="h-4 w-4" aria-hidden />}
        </button>
        <div className="min-w-0 flex-1">
          <JobLine job={job} />
        </div>
      </div>
      {open ? (
        <div className="ml-6">
          <JobDetails
            job={job}
            labels={detailLabels(t)}
            locale={i18n.language}
            trigger={jobTrigger(t, job)}
            stage={job.status === 'Running' ? text.detail : undefined}
            error={job.status === 'Failed' ? (job.errorDetail ?? text.detail) : undefined}
            renderLink={(link, label) => (
              <Link {...linkTarget(link.path)} className="text-primary hover:underline">
                {label}
              </Link>
            )}
          />
          {isGroup ? (
            <div className="border-l bg-muted/20">
              <p className="px-3 pt-2 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                {t('jobs.children')}
              </p>
              {detail.isLoading ? (
                <CenteredSpinner />
              ) : (
                detail.data?.children.map((child) => <JobLine key={child.id} job={child} />)
              )}
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function detailLabels(t: TFunction): JobDetailsLabels {
  return {
    rel: {
      source: t('jobs.detail.source'),
      config: t('jobs.detail.config'),
      run: t('jobs.detail.run'),
      result: t('jobs.detail.result'),
    },
    open: t('jobs.detail.open'),
    trigger: t('jobs.detail.trigger'),
    queued: t('jobs.detail.queued'),
    started: t('jobs.detail.started'),
    finished: t('jobs.detail.finished'),
    duration: t('jobs.detail.duration'),
    attempts: t('jobs.detail.attempts'),
    attemptsValue: (attempt, max) => t('jobs.detail.attemptsValue', { attempt, max }),
    progress: t('jobs.detail.progress'),
    error: t('jobs.detail.error'),
    outcome: t('jobs.detail.outcome'),
    id: t('jobs.detail.id'),
  };
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
