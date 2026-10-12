import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UsageSnapshot } from '@dcms/ui';
import { api } from '../../lib/api';

/** The snapshot, plus the storage limit and what it counts (media files only). */
export interface WorkspaceUsage extends UsageSnapshot {
  quotaBytes: number;
  mediaBytes: number;
}

export function useUsage() {
  return useQuery({
    queryKey: ['usage'],
    queryFn: () => api.get<WorkspaceUsage>('/admin/usage'),
  });
}

/** Measures now. The answer is the new snapshot, so it goes straight into the cache. */
export function useRecalculateUsage() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<WorkspaceUsage>('/admin/usage/recalculate'),
    onSuccess: (usage) => qc.setQueryData(['usage'], usage),
  });
}
