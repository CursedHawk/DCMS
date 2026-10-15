import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import type { JobStatus } from '@dcms/ui';
import { api } from '../../lib/api';
import { getCurrentTenantSlug } from '../../tenants';

/** Mirrors admin-api's JobDto. */
export interface Job {
  id: string;
  tenantId: string | null;
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
}

export interface JobPage {
  items: Job[];
  nextCursor: string | null;
}

export interface JobDetail {
  job: Job;
  children: Job[];
}

/** What the hub pushes: ids and counts, never a title (ADR 0024). Mirrors JobChangedPush. */
export interface JobChanged {
  id: string;
  tenantId: string | null;
  parentId: string | null;
  status: JobStatus;
  done: number;
  total: number | null;
  stageKey: string | null;
  seq: number;
}

export type JobListFilter = 'active' | 'finished' | 'failed' | 'all';

const ACTIVE: ReadonlySet<JobStatus> = new Set(['Queued', 'Running']);

/** Tenant-scoped like the bell: a tenant switch must not show the previous tenant's jobs. */
export const jobsKey = () => ['jobs', getCurrentTenantSlug()] as const;
export const jobListKey = (filter: JobListFilter, limit: number) => [...jobsKey(), 'list', filter, limit] as const;
export const jobKey = (id: string) => [...jobsKey(), 'detail', id] as const;

function listPath(filter: JobListFilter, limit: number, cursor?: string | null) {
  const q = new URLSearchParams({ limit: String(limit) });
  if (filter !== 'all') q.set('status', filter);
  if (cursor) q.set('cursor', cursor);
  return `/admin/jobs?${q}`;
}

export function useJobs(filter: JobListFilter, limit: number, enabled = true) {
  return useQuery({
    queryKey: jobListKey(filter, limit),
    queryFn: () => api.get<JobPage>(listPath(filter, limit)),
    enabled,
    // The hub pushes every change; this is first load and reconnect only.
    staleTime: 30_000,
  });
}

export function useJob(id: string | undefined) {
  return useQuery({
    queryKey: jobKey(id ?? ''),
    queryFn: () => api.get<JobDetail>(`/admin/jobs/${id}`),
    enabled: !!id,
    staleTime: 30_000,
  });
}

export function useCancelJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.post<{ status: string }>(`/admin/jobs/${id}/cancel`, {}),
    onSettled: () => void qc.invalidateQueries({ queryKey: jobsKey() }),
  });
}

export function useRetryJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.post<{ status: string }>(`/admin/jobs/${id}/retry`, {}),
    onSettled: () => void qc.invalidateQueries({ queryKey: jobsKey() }),
  });
}

/**
 * Folds one pushed change into every cached view of that job.
 *
 * <p>Patches in place where it can — a progress tick must not cost a refetch, or a dozen running
 * imports would turn the socket into a request storm. It refetches only what the change cannot
 * say: a job this browser has not seen (it carries no title), and a job that moved between
 * active and finished (it changes which list it belongs in). Anything older than what the cache
 * already holds, by `seq`, is ignored.</p>
 *
 * @returns Whether anything needs refetching, so the caller can debounce it.
 */
export function applyJobChanged(qc: QueryClient, change: JobChanged): boolean {
  let known = false;
  let moved = false;

  const patch = (job: Job): Job => {
    if (job.id !== change.id || change.seq <= job.seq) {
      if (job.id === change.id) known = true;
      return job;
    }
    known = true;
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

  qc.setQueriesData<JobPage>({ queryKey: [...jobsKey(), 'list'] }, (page) =>
    page ? { ...page, items: page.items.map(patch) } : page,
  );
  qc.setQueriesData<JobDetail>({ queryKey: [...jobsKey(), 'detail'] }, (detail) =>
    detail ? { job: patch(detail.job), children: detail.children.map(patch) } : detail,
  );

  // A child's change also changed its group, which arrives as its own push; nothing else to do
  // for children beyond the patch above.
  return moved || (!known && change.parentId === null);
}
