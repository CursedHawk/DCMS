import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { adminApi } from '../../lib/api';

/**
 * admin-api, not platform-api: it owns the `notifications` schema and the `edge` schema these
 * are raised from. The platform console already calls it for certificates and the audit log.
 */
export type PlatformNotificationSeverity = 'Info' | 'Success' | 'Warning' | 'Error';

export interface PlatformNotification {
  id: string;
  /** Discriminator, e.g. `certificate.failed`. Chooses the sentence and the icon. */
  kind: string;
  severity: PlatformNotificationSeverity;
  /** Raw JSON of the facts; shape varies by `kind`. The server never stores rendered prose. */
  paramsJson: string;
  linkPath: string | null;
  createdAt: string;
  readAt: string | null;
}

export interface PlatformNotificationPage {
  items: PlatformNotification[];
  unreadCount: number;
  nextCursor: string | null;
}

const KEY = ['platform-notifications'];

/**
 * Polled rather than pushed.
 *
 * The tenant bell holds a SignalR connection because a tenant admin waiting on a build wants
 * the toast the second it lands. What arrives here is a certificate changing state — a handful
 * of events a week, none of which is worth watching a socket for. A poll also costs no
 * connection through the edge and no group management for an audience defined by a global role.
 */
export function usePlatformNotifications(enabled: boolean, limit = 20) {
  return useQuery({
    queryKey: [...KEY, limit],
    enabled,
    queryFn: () =>
      adminApi.get<PlatformNotificationPage>(`/admin/platform/notifications?limit=${limit}`),
    refetchInterval: 60_000,
  });
}

export function useMarkRead() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => adminApi.post<void>(`/admin/platform/notifications/${id}/read`, {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useMarkAllRead() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      adminApi.post<{ updated: number }>('/admin/platform/notifications/read-all', {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useDismiss() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      adminApi.post<void>(`/admin/platform/notifications/${id}/dismiss`, {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

/**
 * The facts a notification carries. Returns an empty object rather than throwing: a malformed
 * param bag should cost the notification its detail, not remove it from the bell.
 */
export function parseParams(paramsJson: string): Record<string, unknown> {
  try {
    const parsed: unknown = JSON.parse(paramsJson);
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}
