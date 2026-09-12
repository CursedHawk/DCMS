import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { platformApi } from '../../lib/api';

export interface StoreRow {
  store: string;
  usedBytes: number;
  budgetBytes: number;
  peakBytes: number;
  projectedBytes: number;
  growthBytesPerSecond: number;
  retentionSeconds: number;
  canPurge: boolean;
  purgeNote: string | null;
}

export interface StoresResponse {
  stores: StoreRow[];
  dockerLogPurgeAvailable: boolean;
  prometheusUnreachable: boolean;
  /** Prometheus answered, but the store-usage sidecar has not reported yet. */
  awaitingStoreMetrics: boolean;
}

export interface LokiDeleteRequest {
  requestId: string;
  query: string;
  startTime: string;
  endTime: string;
  status: string;
}

export function useStores() {
  return useQuery({
    queryKey: ['platform-stores'],
    queryFn: () => platformApi.get<StoresResponse>('/stores'),
    // Object-store usage comes from Prometheus, which announces nothing — so the period lives
    // in `PlatformSampleBroadcaster` and arrives here as the `stores` tag.
  });
}

export function usePendingPurges() {
  return useQuery({
    queryKey: ['platform-loki-purges'],
    queryFn: () => platformApi.get<LokiDeleteRequest[]>('/purge/loki'),
    // The two-hour cancellation window is the safety net, and a stale list is a net nobody can
    // see — so this stays on the same 30-second cadence it always had. Loki does not announce
    // when it works through the queue either, so that cadence is now the `stores` sample tick
    // rather than a timer in each open tab.
  });
}

export function usePurgeLoki() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { selector: string; start: string; end: string }) =>
      platformApi.post<{ effectiveSelector: string; message: string }>('/purge/loki', body),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['platform-loki-purges'] }),
  });
}

export function useCancelPurge() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (requestId: string) => platformApi.del<void>(`/purge/loki/${encodeURIComponent(requestId)}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['platform-loki-purges'] }),
  });
}

export function useTruncateContainerLog() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (container: string) =>
      platformApi.post<{ freedBytes: number; message: string }>('/purge/docker-logs', { container }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['platform-stores'] }),
  });
}

export function usePruneAnalytics() {
  return useMutation({
    mutationFn: (olderThanDays: number) =>
      platformApi.post<{ deleted: number; note: string }>('/ops/analytics/prune', { olderThanDays }),
  });
}
