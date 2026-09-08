import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { platformApi } from '../../lib/api';

/**
 * Served by platform-api, performed by admin-api, which owns the `notifications` schema these
 * rows live in and the `edge` schema they are raised from. platform-api checks the operator
 * against platform:notifications:read and forwards; read and dismiss state is keyed on the
 * operator's own user id, which travels in the propagated actor headers.
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
      platformApi.get<PlatformNotificationPage>(`/notifications?limit=${limit}`),
    refetchInterval: 60_000,
  });
}

export function useMarkRead() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => platformApi.post<void>(`/notifications/${id}/read`, {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useMarkAllRead() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      platformApi.post<{ updated: number }>('/notifications/read-all', {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useDismiss() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      platformApi.post<void>(`/notifications/${id}/dismiss`, {}),
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
