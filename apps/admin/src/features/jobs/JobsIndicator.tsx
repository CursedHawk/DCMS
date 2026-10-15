import { useNavigate } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { JobsIndicator as Indicator } from '@dcms/ui';
import { type Job, useCancelJob, useJobs, useRetryJob } from './api';
import { linkTarget } from '../notifications/linkPath';
import { jobText } from './text';

/** How many finished jobs the popover keeps under "Recently finished". */
const RECENT = 8;

/**
 * The tenant's jobs ring, beside the bell. The ring, popover and rows are `@dcms/ui`'s — the
 * console draws the same thing across every tenant. This decides where the rows come from and
 * how a job's text is produced; the hub keeps both queries current (`useNotificationHub`).
 */
export function JobsIndicator({ enabled }: { enabled: boolean }) {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const active = useJobs('active', 100, enabled);
  const recent = useJobs('finished', RECENT, enabled);
  const cancel = useCancelJob();
  const retry = useRetryJob();

  return (
    <Indicator<Job>
      enabled={enabled}
      active={active.data?.items ?? []}
      recent={recent.data?.items ?? []}
      isLoading={active.isLoading}
      locale={i18n.language}
      labels={{
        title: t('jobs.title'),
        active: (count) => t('jobs.active', { count }),
        running: t('jobs.running'),
        queued: t('jobs.queued'),
        recent: t('jobs.recent'),
        empty: t('jobs.empty'),
        viewAll: t('jobs.viewAll'),
        cancel: t('jobs.cancel'),
        retry: t('jobs.retry'),
        loading: t('common.loading'),
        status: (s) => t(`jobs.status.${s}`),
      }}
      renderItem={(job) => jobText(t, job)}
      onCancel={(job) =>
        cancel.mutate(job.id, {
          onSuccess: (r) => toast(t(r.status === 'canceled' ? 'jobs.canceled' : 'jobs.cancelRequested')),
          onError: () => toast.error(t('jobs.notCancelable')),
        })
      }
      onRetry={(job) =>
        retry.mutate(job.id, {
          onSuccess: () => toast(t('jobs.retried')),
          onError: () => toast.error(t('jobs.notRetryable')),
        })
      }
      // Where the work's result lives when it has one (the site, the media library), else the job.
      onOpenItem={(job) => void navigate(linkTarget(job.linkPath ?? `/jobs?job=${job.id}`))}
      onViewAll={() => void navigate({ to: '/jobs' as string })}
    />
  );
}
