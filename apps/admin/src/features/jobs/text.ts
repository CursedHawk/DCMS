import type { TFunction } from 'i18next';
import type { Job } from './api';

/**
 * A job's title and its second line, from the keys the server stored. Every key falls back to
 * something readable: a console older than its server meets kinds it has no text for, and the
 * raw kind is a better title than an empty row.
 */
export function jobText(t: TFunction, job: Job): { title: string; detail?: string; origin?: string } {
  const title = t(job.titleKey, { ...job.params, defaultValue: job.kind });
  const origin = jobOrigin(t, job);
  if (job.status === 'Failed') {
    return { title, origin, detail: t(job.errorKey ?? 'jobs.error.failed', { defaultValue: job.errorDetail ?? job.errorKey ?? '' }) };
  }
  if (job.status === 'Running' && job.stageKey) {
    return { title, origin, detail: t(job.stageKey, { ...job.params, defaultValue: '' }) || undefined };
  }
  return { title, origin };
}

/** "by ana@example.com", "via the dynamic-apps plugin" or "automatic". */
function jobOrigin(t: TFunction, job: Job): string {
  const { actorKind, actorName } = job.origin;
  if (actorKind === 'System' || !actorName) return t('jobs.origin.automatic');
  if (actorKind === 'Plugin') return t('jobs.origin.plugin', { name: actorName });
  return t('jobs.origin.by', { name: actorName });
}
