import type { TFunction } from 'i18next';
import type { Job } from './api';

/**
 * A job's title and its second line, from the keys the server stored. Every key falls back to
 * something readable: a console older than its server meets kinds it has no text for, and the
 * raw kind is a better title than an empty row.
 */
export function jobText(t: TFunction, job: Job): { title: string; detail?: string } {
  const title = t(job.titleKey, { ...job.params, defaultValue: job.kind });
  if (job.status === 'Failed') {
    return { title, detail: t(job.errorKey ?? 'jobs.error.failed', { defaultValue: job.errorDetail ?? job.errorKey ?? '' }) };
  }
  if (job.status === 'Running' && job.stageKey) {
    return { title, detail: t(job.stageKey, { ...job.params, defaultValue: '' }) || undefined };
  }
  return { title };
}
