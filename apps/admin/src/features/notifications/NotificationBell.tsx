import { useNavigate } from '@tanstack/react-router';
import {
  Bell,
  CheckCheck,
  CircleAlert,
  CircleCheck,
  Info,
  TriangleAlert,
  X,
} from 'lucide-react';
import { useState } from 'react';
import { relativeTime } from '@dcms/core';
import { useTranslation } from 'react-i18next';
import {
  Button,
  cn,
  Popover,
  PopoverContent,
  PopoverTrigger,
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from '@dcms/ui';
import {
  type Notification,
  type NotificationSeverity,
  parseParams,
  useDismiss,
  useMarkAllRead,
  useMarkRead,
  useNotifications,
} from './api';
import { linkTarget } from './linkPath';

const severityIcon: Record<NotificationSeverity, typeof Info> = {
  Info,
  Success: CircleCheck,
  Warning: TriangleAlert,
  Error: CircleAlert,
};

const severityTone: Record<NotificationSeverity, string> = {
  Info: 'text-muted-foreground',
  Success: 'text-[hsl(var(--success))]',
  Warning: 'text-[hsl(var(--warning))]',
  Error: 'text-destructive',
};

export function NotificationBell({ enabled }: { enabled: boolean }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const { data, isLoading } = useNotifications(enabled);
  const markRead = useMarkRead();
  const markAllRead = useMarkAllRead();
  const dismiss = useDismiss();
  const navigate = useNavigate();

  const items = data?.items ?? [];
  const unread = data?.unreadCount ?? 0;

  const openNotification = (n: Notification) => {
    if (!n.readAt) markRead.mutate(n.id);
    setOpen(false);
    if (n.linkPath) void navigate(linkTarget(n.linkPath));
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      {/* Tooltip and Popover triggers are both asChild Radix Slots on the same Button, so
          their pointer handlers compose. Wrapping the Button in a plain element here would
          swallow the popover's handlers and it would never open — the same trap documented
          on the theme switcher in Topbar. */}
      <Tooltip>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              variant="ghost"
              size="icon"
              className="relative"
              aria-label={
                unread > 0 ? t('notifications.unreadCount', { count: unread }) : t('notifications.title')
              }
            >
              <Bell className="h-4 w-4" />
              {unread > 0 ? (
                <span
                  aria-hidden
                  className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-destructive px-1 text-[10px] font-semibold leading-none text-destructive-foreground"
                >
                  {unread > 99 ? '99+' : unread}
                </span>
              ) : null}
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent>{t('notifications.title')}</TooltipContent>
      </Tooltip>

      <PopoverContent align="end" className="w-96 p-0">
        <div className="flex items-center justify-between border-b px-3 py-2">
          <span className="text-sm font-semibold">{t('notifications.title')}</span>
          {unread > 0 ? (
            <button
              type="button"
              onClick={() => markAllRead.mutate()}
              className="inline-flex items-center gap-1 text-xs text-muted-foreground transition-colors hover:text-foreground"
            >
              <CheckCheck className="h-3.5 w-3.5" />
              {t('notifications.markAllRead')}
            </button>
          ) : null}
        </div>

        <div className="max-h-96 overflow-y-auto">
          {isLoading ? (
            <p className="px-3 py-8 text-center text-sm text-muted-foreground">{t('common.loading')}</p>
          ) : items.length === 0 ? (
            <p className="px-3 py-8 text-center text-sm text-muted-foreground">{t('notifications.empty')}</p>
          ) : (
            items.map((n) => (
              <NotificationRow
                key={n.id}
                notification={n}
                onOpen={() => openNotification(n)}
                onDismiss={() => dismiss.mutate(n.id)}
              />
            ))
          )}
        </div>

        <div className="border-t px-3 py-2">
          <button
            type="button"
            onClick={() => {
              setOpen(false);
              // Cast as in Topbar's /account navigation: TanStack Router's generated route
              // union does not include routes reached only from a lazily-imported module.
              void navigate({ to: '/notifications' as string });
            }}
            className="w-full text-center text-xs text-muted-foreground transition-colors hover:text-foreground"
          >
            {t('notifications.viewAll')}
          </button>
        </div>
      </PopoverContent>
    </Popover>
  );
}

function NotificationRow({
  notification,
  onOpen,
  onDismiss,
}: {
  notification: Notification;
  onOpen: () => void;
  onDismiss: () => void;
}) {
  const { t, i18n } = useTranslation();
  const params = parseParams(notification.paramsJson);
  const Icon = severityIcon[notification.severity] ?? Info;

  return (
    <div
      className={cn(
        'group flex items-start gap-2 border-b px-3 py-2.5 last:border-b-0',
        !notification.readAt && 'bg-primary/[0.04]',
      )}
    >
      <Icon className={cn('mt-0.5 h-4 w-4 shrink-0', severityTone[notification.severity])} />

      <button type="button" onClick={onOpen} className="min-w-0 flex-1 text-left">
        <p className={cn('truncate text-sm', !notification.readAt && 'font-medium')}>
          {t(notification.titleKey, params)}
        </p>
        {/* The short description the bell exists to show; the full text is behind the link. */}
        <p className="mt-0.5 line-clamp-2 text-xs text-muted-foreground">
          {t(notification.bodyKey, params)}
        </p>
        <p className="mt-1 text-[11px] text-muted-foreground/70">
          {relativeTime(notification.createdAt, i18n.language)}
        </p>
      </button>

      <button
        type="button"
        onClick={onDismiss}
        aria-label={t('notifications.dismiss')}
        className="opacity-0 transition-opacity hover:text-foreground group-hover:opacity-100 focus-visible:opacity-100"
      >
        <X className="h-3.5 w-3.5 text-muted-foreground" />
      </button>
    </div>
  );
}

