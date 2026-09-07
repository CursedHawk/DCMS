import { useState } from 'react';
import { BellOff } from 'lucide-react';
import { Button, CenteredSpinner, EmptyState } from '@dcms/ui';
import { useDismiss, useMarkAllRead, useMarkRead, usePlatformNotifications } from './api';
import { NotificationRow } from './NotificationBell';
import { useMe } from '../../lib/permissions';
import { useNavigate } from '@tanstack/react-router';

/**
 * Everything the bell has been holding, in one place.
 *
 * <p>The bell shows twenty; this shows a hundred and can filter to what is still unread, which
 * is the view for "what has this platform been trying to tell me since Friday".</p>
 */
export function NotificationsPage() {
  const me = useMe(true);
  const isSuperAdmin = me.data?.isSuperAdmin ?? false;
  const [unreadOnly, setUnreadOnly] = useState(false);
  const q = usePlatformNotifications(isSuperAdmin, 100);
  const markRead = useMarkRead();
  const markAllRead = useMarkAllRead();
  const dismiss = useDismiss();
  const navigate = useNavigate();

  if (me.isLoading) return <CenteredSpinner />;

  if (!isSuperAdmin) {
    return (
      <EmptyState
        icon={BellOff}
        title="Not available"
        description="Platform notifications are visible to superadmins."
      />
    );
  }

  const all = q.data?.items ?? [];
  const items = unreadOnly ? all.filter((n) => !n.readAt) : all;
  const unread = q.data?.unreadCount ?? 0;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-lg font-semibold">Notifications</h1>
          <p className="mt-1 max-w-2xl text-sm text-muted-foreground">
            What the platform has been unable to do for itself, and what it did. Certificate
            issuance, refusals and expiries land here; the tenant consoles have their own.
          </p>
        </div>
        <div className="flex shrink-0 gap-2">
          <Button variant="outline" size="sm" onClick={() => setUnreadOnly((v) => !v)}>
            {unreadOnly ? 'Show all' : `Unread only${unread > 0 ? ` (${unread})` : ''}`}
          </Button>
          {unread > 0 && (
            <Button variant="outline" size="sm" onClick={() => markAllRead.mutate()}>
              Mark all read
            </Button>
          )}
        </div>
      </div>

      {q.isLoading ? (
        <CenteredSpinner />
      ) : items.length === 0 ? (
        <EmptyState
          icon={BellOff}
          title={unreadOnly ? 'Nothing unread' : 'Nothing to report'}
          description="Certificate failures, refusals and expiries appear here as they happen."
        />
      ) : (
        <div className="overflow-hidden rounded-md border border-border bg-card">
          {items.map((n) => (
            <NotificationRow
              key={n.id}
              notification={n}
              onOpen={() => {
                if (!n.readAt) markRead.mutate(n.id);
                if (n.linkPath) void navigate({ to: n.linkPath as string });
              }}
              onDismiss={() => dismiss.mutate(n.id)}
            />
          ))}
        </div>
      )}
    </div>
  );
}
