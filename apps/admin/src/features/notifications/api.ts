import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api';
import { getCurrentTenantSlug } from '../../tenants';

export type NotificationSeverity = 'Info' | 'Success' | 'Warning' | 'Error';

export interface Notification {
  id: string;
  kind: string;
  severity: NotificationSeverity;
  /** i18n key, resolved client-side — the server never stores rendered prose. */
  titleKey: string;
  bodyKey: string;
  /** Raw JSON string of interpolation values; shape varies by `kind`. */
  paramsJson: string;
  linkPath: string | null;
  resourceType: string | null;
  resourceId: string | null;
  /** Who caused it. Used to suppress a toast for your own actions. */
  actorUserId: string | null;
  createdAt: string;
  readAt: string | null;
  dismissedAt: string | null;
}

export interface NotificationPage {
  items: Notification[];
  unreadCount: number;
  nextCursor: string | null;
}

/** Query key root. Tenant-scoped because a tenant switch must not reuse the previous bell. */
export const notificationsKey = () => ['notifications', getCurrentTenantSlug()] as const;

export function useNotifications(enabled: boolean) {
  return useQuery({
    queryKey: notificationsKey(),
    queryFn: () => api.get<NotificationPage>('/admin/notifications?limit=20'),
    enabled,
    // No interval of any kind: the hub pushes. This is the reconnect/first-load path only, and
    // `useNotificationHub` invalidates this key when the socket delivers something.
    staleTime: 30_000,
  });
}

export function useMarkRead() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.post<void>(`/admin/notifications/${id}/read`, {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: notificationsKey() }),
  });
}

export function useMarkAllRead() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<{ updated: number }>('/admin/notifications/read-all', {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: notificationsKey() }),
  });
}

export function useDismiss() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.post<void>(`/admin/notifications/${id}/dismiss`, {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: notificationsKey() }),
  });
}

/**
 * Interpolation values for a notification's i18n keys. Returns an empty object rather than
 * throwing when the JSON is unparseable — a malformed param bag should cost the notification
 * its detail, not remove it from the bell.
 */
export function parseParams(paramsJson: string): Record<string, unknown> {
  try {
    const parsed: unknown = JSON.parse(paramsJson);
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}
