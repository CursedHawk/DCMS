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

/** "by ana@example.com via admin-api · trace 4bf92f3577b3". */
export function jobOrigin(job: Job): string {
  const { actorKind, actorName, service, traceId } = job.origin;
  const who = job.routine
    ? 'on a schedule'
    : actorKind === 'System' || !actorName
      ? 'automatic'
      : actorKind === 'Plugin'
        ? `plugin ${actorName}`
        : actorKind === 'User' || actorKind === 'EndUser'
          ? `by ${actorName}`
          : `by ${actorName} (${actorKind})`;
  return `${who}${service ? ` via ${service}` : ''}${traceId ? ` · trace ${traceId.slice(0, 12)}` : ''}`;
}

/** The details' "Trigger": "On a schedule", "Started by ana@example.com", "Automatic". */
export function jobTrigger(job: Job): string {
  const { actorKind, actorName } = job.origin;
  if (job.routine) return 'On a schedule';
  if (actorKind === 'System' || !actorName) return 'Automatic';
  if (actorKind === 'Plugin') return `Started by the ${actorName} plugin`;
  return `Started by ${actorName}${actorKind === 'User' || actorKind === 'EndUser' ? '' : ` (${actorKind})`}`;
}

export const STATUS_LABEL: Record<Job['status'], string> = {
  Queued: 'Queued',
  Running: 'Running',
  Succeeded: 'Done',
  Failed: 'Failed',
  Canceled: 'Canceled',
};
