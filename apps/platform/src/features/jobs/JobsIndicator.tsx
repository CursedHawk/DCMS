import { useNavigate } from '@tanstack/react-router';
import { toast } from 'sonner';
import { JobsIndicator as Indicator } from '@dcms/ui';
import { type Job, useCancelJob, useJobs, useRetryJob } from './api';
import { jobDetail, jobTitle, STATUS_LABEL } from './render';

/**
 * Every tenant's jobs and the platform's own, in the console's top bar: the same ring and
 * popover as the tenant admin, across the whole platform. Cancel and retry appear only for an
 * operator holding platform:jobs:manage; the server checks again.
 */
export function JobsIndicator({ enabled, mayManage }: { enabled: boolean; mayManage: boolean }) {
  const navigate = useNavigate();
  const active = useJobs('active', 100, {}, enabled);
  const recent = useJobs('finished', 8, {}, enabled);
  const cancel = useCancelJob();
  const retry = useRetryJob();

  return (
    <Indicator<Job>
      enabled={enabled}
      active={active.data?.items ?? []}
      recent={recent.data?.items ?? []}
      isLoading={active.isLoading}
      labels={{
        title: 'Background jobs',
        active: (n) => `${n} background job${n === 1 ? '' : 's'} in progress`,
        running: 'Running',
        queued: 'Queued',
        recent: 'Recently finished',
        empty: 'Nothing is running on the platform.',
        viewAll: 'All background jobs',
        cancel: 'Cancel',
        retry: 'Retry',
        loading: 'Loading…',
        status: (s) => STATUS_LABEL[s],
      }}
      renderItem={(job) => ({ title: jobTitle(job), detail: jobDetail(job) })}
      onCancel={
        mayManage
          ? (job) =>
              cancel.mutate(job.id, {
                onSuccess: (r) => toast(r.status === 'canceled' ? 'Job canceled.' : 'Stopping the job…'),
                onError: () => toast.error('This job can no longer be canceled.'),
              })
          : undefined
      }
      onRetry={
        mayManage
          ? (job) =>
              retry.mutate(job.id, {
                onSuccess: () => toast('Job queued again.'),
                onError: () => toast.error("This job can't be retried from here."),
              })
          : undefined
      }
      onOpenItem={(job) => void navigate({ to: '/jobs', search: { job: job.id } as never })}
      onViewAll={() => void navigate({ to: '/jobs' })}
    />
  );
}
