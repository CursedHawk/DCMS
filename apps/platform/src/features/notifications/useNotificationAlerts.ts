import { useEffect, useRef } from 'react';
import { toast } from 'sonner';
import { usePlatformNotifications } from './api';
import { selectAlerts } from './announce';
import { render } from './render';

/**
 * Tells an operator when the platform has gone wrong, instead of waiting for them to look.
 *
 * <p>Shares the bell's query rather than adding one: same key, same cache entry, so the socket
 * push that refreshes the badge is also what fires the toast. See {@link selectAlerts} for the
 * two rules — a baseline on first sight, and severe-and-unread only.</p>
 */
export function useNotificationAlerts(enabled: boolean) {
  const q = usePlatformNotifications(enabled);

  // null until the first page arrives, which is what makes the first observation a baseline
  // rather than a burst of toasts for a backlog.
  const seenRef = useRef<Set<string> | null>(null);

  useEffect(() => {
    if (!q.data) return;

    const { alerts, seen } = selectAlerts(q.data.items, seenRef.current);
    seenRef.current = seen;

    for (const n of alerts) {
      const { title, body } = render(n);
      const options = { description: body };
      if (n.severity === 'Error') toast.error(title, options);
      else toast.warning(title, options);
    }
  }, [q.data]);
}
