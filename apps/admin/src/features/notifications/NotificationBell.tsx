import { useNavigate } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { NotificationBell as Bell } from '@dcms/ui';
import {
  type Notification,
  parseParams,
  useDismiss,
  useMarkAllRead,
  useMarkRead,
  useNotifications,
} from './api';
import { linkTarget } from './linkPath';

/**
 * The tenant bell.
 *
 * The popover, the badge, the severity icons and the read/unread treatment are `@dcms/ui`'s —
 * the platform console draws the identical thing against a different backend. What is this
 * app's is where the rows come from and how their text is produced: these carry i18n keys and
 * a params bag, so a notification stays translatable after the entity it names is renamed.
 */
export function NotificationBell({ enabled }: { enabled: boolean }) {
  const { t, i18n } = useTranslation();
  const { data, isLoading } = useNotifications(enabled);
  const markRead = useMarkRead();
  const markAllRead = useMarkAllRead();
  const dismiss = useDismiss();
  const navigate = useNavigate();

  return (
    <Bell<Notification>
      enabled={enabled}
      items={data?.items ?? []}
      unreadCount={data?.unreadCount ?? 0}
      isLoading={isLoading}
      locale={i18n.language}
      labels={{
        title: t('notifications.title'),
        markAllRead: t('notifications.markAllRead'),
        empty: t('notifications.empty'),
        viewAll: t('notifications.viewAll'),
        dismiss: t('notifications.dismiss'),
        loading: t('common.loading'),
        unread: (count) => t('notifications.unreadCount', { count }),
      }}
      renderItem={(n) => {
        const params = parseParams(n.paramsJson);
        return { title: t(n.titleKey, params), body: t(n.bodyKey, params) };
      }}
      onOpenItem={(n) => {
        if (!n.readAt) markRead.mutate(n.id);
        if (n.linkPath) void navigate(linkTarget(n.linkPath));
      }}
      onDismiss={(n) => dismiss.mutate(n.id)}
      onMarkAllRead={() => markAllRead.mutate()}
      // Cast as in the account navigation: TanStack Router's generated route union does not
      // include routes reached only from a lazily-imported module.
      onViewAll={() => void navigate({ to: '/notifications' as string })}
    />
  );
}
