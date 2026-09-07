import { useNavigate } from '@tanstack/react-router';
import { Bell, CheckCheck, CircleAlert, CircleCheck, Info, TriangleAlert, X } from 'lucide-react';
import { useState } from 'react';
import { Button, cn, Popover, PopoverContent, PopoverTrigger } from '@dcms/ui';
import {
  type PlatformNotification,
  type PlatformNotificationSeverity,
  useDismiss,
  useMarkAllRead,
  useMarkRead,
  usePlatformNotifications,
} from './api';
import { render } from './render';

const severityIcon: Record<PlatformNotificationSeverity, typeof Info> = {
  Info,
  Success: CircleCheck,
  Warning: TriangleAlert,
  Error: CircleAlert,
};

const severityTone: Record<PlatformNotificationSeverity, string> = {
  Info: 'text-muted-foreground',
  Success: 'text-[hsl(var(--success))]',
  Warning: 'text-[hsl(var(--warning))]',
  Error: 'text-destructive',
};

/**
 * The platform console's bell.
 *
 * <p><b>Opening it does not mark anything read.</b> That is the whole point of the badge: these
 * are the platform's own failures, and the number has to survive somebody glancing at the bell
 * and closing it again. It clears when an operator opens an item, dismisses it, or presses
 * "Mark all read" — three deliberate acts, none of them a glance.</p>
 */
export function NotificationBell({ enabled }: { enabled: boolean }) {
  const [open, setOpen] = useState(false);
  const { data, isLoading } = usePlatformNotifications(enabled);
  const markRead = useMarkRead();
  const markAllRead = useMarkAllRead();
  const dismiss = useDismiss();
  const navigate = useNavigate();

  if (!enabled) return null;

  const items = data?.items ?? [];
  const unread = data?.unreadCount ?? 0;

  const openNotification = (n: PlatformNotification) => {
    if (!n.readAt) markRead.mutate(n.id);
    setOpen(false);
    // Cast for the same reason as the admin console's: TanStack Router's generated route union
    // does not include routes reached only from a lazily-imported module.
    if (n.linkPath) void navigate({ to: n.linkPath as string });
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="ghost"
          size="sm"
          className="relative"
          aria-label={unread > 0 ? `Notifications, ${unread} unread` : 'Notifications'}
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

      <PopoverContent align="end" className="w-96 p-0">
        <div className="flex items-center justify-between border-b border-border px-3 py-2">
          <span className="text-sm font-semibold">Notifications</span>
          {unread > 0 ? (
            <button
              type="button"
              onClick={() => markAllRead.mutate()}
              className="inline-flex items-center gap-1 text-xs text-muted-foreground transition-colors hover:text-foreground"
            >
              <CheckCheck className="h-3.5 w-3.5" aria-hidden />
              Mark all read
            </button>
          ) : null}
        </div>

        <div className="max-h-96 overflow-y-auto">
          {isLoading ? (
            <p className="px-3 py-8 text-center text-sm text-muted-foreground">Loading…</p>
          ) : items.length === 0 ? (
            <p className="px-3 py-8 text-center text-sm text-muted-foreground">
              Nothing to report. Certificate failures and expiries appear here.
            </p>
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

        <div className="border-t border-border px-3 py-2">
          <button
            type="button"
            onClick={() => {
              setOpen(false);
              void navigate({ to: '/notifications' as string });
            }}
            className="w-full text-center text-xs text-muted-foreground transition-colors hover:text-foreground"
          >
            View all
          </button>
        </div>
      </PopoverContent>
    </Popover>
  );
}

export function NotificationRow({
  notification,
  onOpen,
  onDismiss,
}: {
  notification: PlatformNotification;
  onOpen: () => void;
  onDismiss: () => void;
}) {
  const { title, body } = render(notification);
  const Icon = severityIcon[notification.severity] ?? Info;

  return (
    <div
      className={cn(
        'group flex items-start gap-2 border-b border-border px-3 py-2.5 last:border-b-0',
        !notification.readAt && 'bg-primary/[0.04]',
      )}
    >
      <Icon className={cn('mt-0.5 h-4 w-4 shrink-0', severityTone[notification.severity])} />

      <button type="button" onClick={onOpen} className="min-w-0 flex-1 text-left">
        <p className={cn('truncate text-sm', !notification.readAt && 'font-medium')}>{title}</p>
        {body ? <p className="mt-0.5 line-clamp-2 text-xs text-muted-foreground">{body}</p> : null}
        <p className="mt-1 text-[11px] text-muted-foreground/70">
          {formatRelative(notification.createdAt)}
        </p>
      </button>

      <button
        type="button"
        onClick={onDismiss}
        aria-label="Dismiss"
        className="opacity-0 transition-opacity hover:text-foreground group-hover:opacity-100 focus-visible:opacity-100"
      >
        <X className="h-3.5 w-3.5 text-muted-foreground" />
      </button>
    </div>
  );
}

/**
 * Coarse relative time. Deliberately not a live-ticking clock: the bell can hold twenty rows and
 * a per-row interval would re-render the popover every second for nothing.
 */
export function formatRelative(iso: string): string {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return 'just now';
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days}d ago`;
  return new Date(iso).toLocaleDateString();
}
