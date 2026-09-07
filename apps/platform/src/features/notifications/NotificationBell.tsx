import { useNavigate } from '@tanstack/react-router';
import { NotificationBell as Bell } from '@dcms/ui';
import {
  type PlatformNotification,
  useDismiss,
  useMarkAllRead,
  useMarkRead,
  usePlatformNotifications,
} from './api';
import { render } from './render';

/**
 * The console's bell.
 *
 * Same component as the tenant admin's, different source and different text: these rows carry
 * a kind and a bag of facts, and `render.ts` turns them into a sentence here rather than
 * storing prose the platform can never improve. This console ships one language, so there are
 * no keys to look up — the shape is the one i18n would need, so that stays possible.
 */
export function NotificationBell({ enabled }: { enabled: boolean }) {
  const { data, isLoading } = usePlatformNotifications(enabled);
  const markRead = useMarkRead();
  const markAllRead = useMarkAllRead();
  const dismiss = useDismiss();
  const navigate = useNavigate();

  return (
    <Bell<PlatformNotification>
      enabled={enabled}
      items={data?.items ?? []}
      unreadCount={data?.unreadCount ?? 0}
      isLoading={isLoading}
      labels={{
        title: 'Notifications',
        markAllRead: 'Mark all read',
        empty: 'Nothing to report. Certificate failures and expiries appear here.',
        viewAll: 'View all',
        dismiss: 'Dismiss',
        loading: 'Loading…',
        unread: (count) => `Notifications, ${count} unread`,
      }}
      renderItem={(n) => render(n)}
      onOpenItem={(n) => {
        if (!n.readAt) markRead.mutate(n.id);
        if (n.linkPath) void navigate({ to: n.linkPath as string });
      }}
      onDismiss={(n) => dismiss.mutate(n.id)}
      onMarkAllRead={() => markAllRead.mutate()}
      onViewAll={() => void navigate({ to: '/notifications' as string })}
    />
  );
}
