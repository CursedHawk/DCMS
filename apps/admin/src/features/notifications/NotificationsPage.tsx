import { useNavigate } from '@tanstack/react-router';
import { Bell, CheckCheck, CircleAlert, CircleCheck, Info, TriangleAlert } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, CenteredSpinner, cn, EmptyState, Page, PageHeader } from '@dcms/ui';
import {
  type Notification,
  type NotificationSeverity,
  parseParams,
  useMarkAllRead,
  useMarkRead,
  useNotifications,
} from './api';
import { linkTarget } from './linkPath';
import { formatRelative } from './NotificationBell';

const severityIcon: Record<NotificationSeverity, typeof Info> = {
  Info,
  Success: CircleCheck,
  Warning: TriangleAlert,
  Error: CircleAlert,
};

const severityTone: Record<NotificationSeverity, 'secondary' | 'success' | 'warning' | 'destructive'> = {
  Info: 'secondary',
  Success: 'success',
  Warning: 'warning',
  Error: 'destructive',
};

/**
 * The full notification list and detail view — the "clickable for full description" half of
 * the bell. The popover truncates to two lines; this shows the whole thing plus the link to
 * whatever the notification is about.
 */
export function NotificationsPage() {
  const { t } = useTranslation();
  const { data, isLoading } = useNotifications(true);
  const markAllRead = useMarkAllRead();
  const [selected, setSelected] = useState<Notification | null>(null);

  if (isLoading) return <CenteredSpinner />;

  const items = data?.items ?? [];
  const unread = data?.unreadCount ?? 0;

  return (
    <Page>
      <PageHeader
        title={t('notifications.title')}
        description={t('notifications.pageDescription')}
        actions={
          unread > 0 ? (
            <Button variant="outline" size="sm" onClick={() => markAllRead.mutate()}>
              <CheckCheck className="h-4 w-4" />
              {t('notifications.markAllRead')}
            </Button>
          ) : undefined
        }
      />

      {items.length === 0 ? (
        <EmptyState
          icon={Bell}
          title={t('notifications.empty')}
          description={t('notifications.emptyDescription')}
        />
      ) : (
        <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
          <div className="divide-y rounded-md border">
            {items.map((n) => (
              <ListRow
                key={n.id}
                notification={n}
                active={selected?.id === n.id}
                onSelect={() => setSelected(n)}
              />
            ))}
          </div>
          <Detail notification={selected} />
        </div>
      )}
    </Page>
  );
}

function ListRow({
  notification,
  active,
  onSelect,
}: {
  notification: Notification;
  active: boolean;
  onSelect: () => void;
}) {
  const { t } = useTranslation();
  const markRead = useMarkRead();
  const params = parseParams(notification.paramsJson);
  const Icon = severityIcon[notification.severity] ?? Info;

  return (
    <button
      type="button"
      onClick={() => {
        if (!notification.readAt) markRead.mutate(notification.id);
        onSelect();
      }}
      className={cn(
        'flex w-full items-start gap-3 px-4 py-3 text-left transition-colors hover:bg-muted/50',
        active && 'bg-muted',
        !notification.readAt && 'font-medium',
      )}
    >
      <Icon className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm">{t(notification.titleKey, params)}</span>
        <span className="mt-0.5 block truncate text-xs font-normal text-muted-foreground">
          {t(notification.bodyKey, params)}
        </span>
      </span>
      <span className="shrink-0 text-[11px] text-muted-foreground">
        {formatRelative(notification.createdAt, t)}
      </span>
    </button>
  );
}

function Detail({ notification }: { notification: Notification | null }) {
  const { t } = useTranslation();
  const navigate = useNavigate();

  if (!notification) {
    return (
      <div className="hidden rounded-md border p-8 text-center text-sm text-muted-foreground lg:block">
        {t('notifications.selectPrompt')}
      </div>
    );
  }

  const params = parseParams(notification.paramsJson);

  return (
    <div className="rounded-md border p-5">
      <div className="mb-3 flex items-center gap-2">
        <Badge tone={severityTone[notification.severity]}>{notification.severity}</Badge>
        <span className="text-xs text-muted-foreground">
          {new Date(notification.createdAt).toLocaleString()}
        </span>
      </div>

      <h2 className="text-lg font-semibold">{t(notification.titleKey, params)}</h2>
      <p className="mt-2 whitespace-pre-wrap text-sm text-muted-foreground">
        {t(notification.bodyKey, params)}
      </p>

      {notification.linkPath ? (
        <Button
          className="mt-4"
          size="sm"
          onClick={() => void navigate(linkTarget(notification.linkPath as string))}
        >
          {t('notifications.view')}
        </Button>
      ) : null}
    </div>
  );
}
