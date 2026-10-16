import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { patchJobCaches, type JobChangedPush, type JobDetail, type JobDto, type JobPage } from '@dcms/ui';
import { api } from '../../lib/api';
import { getCurrentTenantSlug } from '../../tenants';

export type Job = JobDto;
export type JobChanged = JobChangedPush;
export type { JobDetail, JobPage };

/** `recent` is `finished` less routine successes (a schedule's ticks): the popover's list. */
export type JobListFilter = 'active' | 'finished' | 'recent' | 'failed' | 'all';


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

/** Folds a pushed change into this tenant's cached jobs; true when the lists need refetching. */
export function applyJobChanged(qc: QueryClient, change: JobChanged): boolean {
  return patchJobCaches(qc, jobsKey(), change);
}
