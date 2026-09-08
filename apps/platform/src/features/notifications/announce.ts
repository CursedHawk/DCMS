import type { PlatformNotification } from './api';

/**
 * Which notifications are worth interrupting an operator for, given what they have already
 * been shown this session.
 *
 * <p>Pure, and separate from the hook, because the two rules that matter are both easy to get
 * subtly wrong and impossible to see wrong from the outside.</p>
 *
 * <p><b>The first observation establishes a baseline and announces nothing.</b> Opening the
 * console must not fire a toast for every certificate that has been failing since last week —
 * the bell already carries those, with a badge, which is what a backlog deserves.</p>
 *
 * <p><b>Only Warning and Error, and only while unread.</b> A certificate that renewed is good
 * news that belongs in the bell; a certificate that expired is the reason someone should stop
 * what they are doing. And a notification an operator has already read is one they have already
 * dealt with — re-announcing it after a refetch would make the console nag.</p>
 */
export function selectAlerts(
  items: readonly PlatformNotification[],
  seen: ReadonlySet<string> | null,
): { alerts: PlatformNotification[]; seen: Set<string> } {
  const next = new Set(items.map((n) => n.id));

  if (seen === null) {
    return { alerts: [], seen: next };
  }

  const alerts = items.filter(
    (n) =>
      !seen.has(n.id) &&
      n.readAt === null &&
      (n.severity === 'Warning' || n.severity === 'Error'),
  );

  // Union rather than replacement: a notification that has scrolled off the page the bell
  // fetches must not become announceable again if it comes back into view.
  return { alerts, seen: new Set([...seen, ...next]) };
}
