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
 * Pushed, with nothing behind it but the three moments a push can be missed.
 *
 * The socket (see `useConsoleHub`) invalidates this query the moment a notification is raised,
 * which is what makes the badge immediate. What used to sit behind it was a five-minute timer
 * in every open tab; what sits behind it now is `useHubRevalidation` in the shell, which
 * refetches on reconnect, on the tab becoming visible and on coming back online. Nothing
 * replays what was pushed during an outage, and those are the windows in which one can happen —
 * so covering them is the whole of what the interval was ever doing.
 */
export function usePlatformNotifications(enabled: boolean, limit = 20) {
  return useQuery({
    queryKey: [...KEY, limit],
    enabled,
    queryFn: () =>
      platformApi.get<PlatformNotificationPage>(`/notifications?limit=${limit}`),
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
