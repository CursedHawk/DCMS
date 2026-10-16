import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { patchJobCaches, type JobChangedPush, type JobDetail, type JobPage } from '@dcms/ui';
import { platformApi } from '../../lib/api';

export type { JobDetail, JobDto as Job, JobPage } from '@dcms/ui';

/** `recent` is `finished` less routine successes (a schedule's ticks): the popover's list. */
export type JobListFilter = 'active' | 'finished' | 'recent' | 'failed' | 'all';

/** Which jobs: one tenant's, the platform's own (no tenant), or every one. */
export type JobScope = { tenantId: string } | { platformOnly: true } | Record<string, never>;

export interface TenantJobSummary {
  tenantId: string | null;
  tenantSlug: string | null;
  tenantName: string | null;
  queued: number;
  running: number;
  failedLast24h: number;
}

export interface QueueDepth {
  stream: string;
  /** Empty when nobody drains the stream — the case worth a warning. */
  consumer: string;
  pending: number;
  ackPending: number;
}

export interface JobOverview {
  tenants: TenantJobSummary[];
  queues: QueueDepth[];
}

const ROOT = ['platform-jobs'] as const;

export const jobListKey = (filter: JobListFilter, limit: number, scope: JobScope = {}) =>
  [...ROOT, 'list', filter, limit, scope] as const;

function listPath(filter: JobListFilter, limit: number, scope: JobScope) {
  const q = new URLSearchParams({ limit: String(limit) });
  if (filter !== 'all') q.set('status', filter);
  if ('tenantId' in scope) q.set('tenantId', scope.tenantId);
  if ('platformOnly' in scope) q.set('platformOnly', 'true');
  return `/jobs?${q}`;
}

export function useJobs(filter: JobListFilter, limit: number, scope: JobScope = {}, enabled = true) {
  return useQuery({
    queryKey: jobListKey(filter, limit, scope),
    queryFn: () => platformApi.get<JobPage>(listPath(filter, limit, scope)),
    enabled,
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  });
}

export function useJob(id: string | undefined) {
  return useQuery({
    queryKey: [...ROOT, 'detail', id ?? ''],
    queryFn: () => platformApi.get<JobDetail>(`/jobs/${id}`),
    enabled: !!id,
    staleTime: 30_000,
  });
}

/**
 * Backlog per tenant and per queue. Nothing announces queue depth changing, so the server samples
 * it for open consoles (the `jobqueues` tag, PlatformSampleBroadcaster) rather than this polling.
 */
export function useJobOverview(enabled = true) {
  return useQuery({
    queryKey: [...ROOT, 'overview'],
    queryFn: () => platformApi.get<JobOverview>('/jobs/overview'),
    enabled,
  });
}

export function useCancelJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => platformApi.post<{ status: string }>(`/jobs/${id}/cancel`, {}),
    onSettled: () => void qc.invalidateQueries({ queryKey: ROOT }),
  });
}

export function useRetryJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => platformApi.post<{ status: string }>(`/jobs/${id}/retry`, {}),
    onSettled: () => void qc.invalidateQueries({ queryKey: ROOT }),
  });
}

/** Folds a pushed change into the console's cached jobs; true when the lists need refetching. */
export function applyJobChanged(qc: QueryClient, change: JobChangedPush): boolean {
  return patchJobCaches(qc, ROOT, change);
}

export const jobsRoot = ROOT;
