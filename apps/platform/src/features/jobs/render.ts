import type { Job } from './api';

/**
 * The console's sentence for a job. It ships one language and hardcodes its strings (as
 * notifications/render.ts does), so the stored i18n keys are the admin SPA's business; here the
 * kind reads as its title ("media.drive-import" → "Media drive import").
 */
const humanize = (kind: string) => {
  const words = kind.replace(/[.\-_:]+/g, ' ').trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
};

export function jobTitle(job: Job, withTenant = true): string {
  const title = humanize(job.kind);
  if (!withTenant) return title;
  return `${job.tenantSlug ?? (job.tenantId ? job.tenantId.slice(0, 8) : 'Platform')} · ${title}`;
}

export function jobDetail(job: Job): string | undefined {
  if (job.status === 'Failed') return job.errorDetail ?? job.errorKey ?? undefined;
  if (job.status === 'Running' && job.stageKey) return job.stageKey.split('.').pop();
  return undefined;
}

export const STATUS_LABEL: Record<Job['status'], string> = {
  Queued: 'Queued',
  Running: 'Running',
  Succeeded: 'Done',
  Failed: 'Failed',
  Canceled: 'Canceled',
};
