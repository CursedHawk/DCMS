import type { QueryClient } from '@tanstack/react-query';
import type { JobStatus } from '../shell/JobsIndicator';

/** A background job as admin-api returns it, to either console (ADR 0024). Mirrors JobDto. */
export interface JobDto {
  id: string;
  tenantId: string | null;
  /** Filled for the platform console only. */
  tenantSlug: string | null;
  tenantName: string | null;
  parentId: string | null;
  kind: string;
  lane: string;
  source: string;
  /** i18n key; the server stores no prose. */
  titleKey: string;
  params: Record<string, unknown>;
  status: JobStatus;
  done: number;
  total: number | null;
  stageKey: string | null;
  errorKey: string | null;
  errorDetail: string | null;
  linkPath: string | null;
  cancelable: boolean;
  cancelRequested: boolean;
  retryable: boolean;
  attempts: number;
  maxAttempts: number;
  actorUserId: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  updatedAt: string;
  seq: number;
  /** What the handler returned, e.g. `{ assetId }` for a Drive file. Null until it succeeds. */
  result: unknown;
  /** Who or what started it, from the request it was enqueued under. */
  origin: JobOrigin;
}

export interface JobOrigin {
  /** `User`, `EndUser`, `ServiceClient`, `Webhook`, `Plugin` or `System` (nothing captured). */
  actorKind: string;
  /**
   * A person's email or name; a client, plugin or service id otherwise. A tenant gets only its
   * own members' emails and plugin ids.
   */
  actorName: string | null;
  /** The process that enqueued it, e.g. `admin-api`; the console's only. */
  service: string | null;
  /** The originating request's trace; the console's only. */
  traceId: string | null;
}

export interface JobPage {
  items: JobDto[];
  nextCursor: string | null;
}

export interface JobDetail {
  job: JobDto;
  children: JobDto[];
}

/** What both hubs push as `JobChanged`: ids and counts, never a title. */
export interface JobChangedPush {
  id: string;
  tenantId: string | null;
  parentId: string | null;
  status: JobStatus;
  done: number;
  total: number | null;
  stageKey: string | null;
  seq: number;
}

const ACTIVE: ReadonlySet<JobStatus> = new Set(['Queued', 'Running']);

/**
 * Folds one pushed change into every cached view of that job under `root`: lists at
 * `[...root, 'list', …]` holding a {@link JobPage}, details at `[...root, 'detail', id]` holding a
 * {@link JobDetail}.
 *
 * <p>Patches in place where it can — a progress tick must not cost a refetch, or a dozen running
 * imports would turn the socket into a request storm. It asks for a refetch only for what the
 * change cannot say: a top-level job this browser has not seen (it carries no title), and a job
 * that moved between active and finished (it changes which list it belongs in). Anything older
 * than what the cache already holds, by `seq`, is ignored.</p>
 *
 * @returns Whether to refetch (lists and details: a job that just finished has a result the push
 *   does not carry), so the caller can debounce it.
 */
export function patchJobCaches(qc: QueryClient, root: readonly unknown[], change: JobChangedPush): boolean {
  let known = false;
  let moved = false;

  const patch = (job: JobDto): JobDto => {
    if (job.id !== change.id) return job;
    known = true;
    if (change.seq <= job.seq) return job;
    if (ACTIVE.has(job.status) !== ACTIVE.has(change.status)) moved = true;
    return {
      ...job,
      status: change.status,
      done: change.done,
      total: change.total,
      stageKey: change.stageKey,
      seq: change.seq,
      cancelable: job.cancelable && ACTIVE.has(change.status),
    };
  };

  qc.setQueriesData<JobPage>({ queryKey: [...root, 'list'] }, (page) =>
    page ? { ...page, items: page.items.map(patch) } : page,
  );
  qc.setQueriesData<JobDetail>({ queryKey: [...root, 'detail'] }, (detail) =>
    detail ? { job: patch(detail.job), children: detail.children.map(patch) } : detail,
  );

  return moved || (!known && change.parentId === null);
}
