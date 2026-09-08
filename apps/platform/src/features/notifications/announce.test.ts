import { describe, expect, it } from 'vitest';
import { selectAlerts } from './announce';
import type { PlatformNotification, PlatformNotificationSeverity } from './api';

function notification(
  id: string,
  severity: PlatformNotificationSeverity,
  readAt: string | null = null,
): PlatformNotification {
  return {
    id,
    kind: 'certificate.failed',
    severity,
    paramsJson: '{}',
    linkPath: null,
    createdAt: '2026-09-08T00:00:00Z',
    readAt,
  };
}

describe('selectAlerts', () => {
  it('announces nothing on the first sight of a backlog', () => {
    const items = [notification('a', 'Error'), notification('b', 'Warning')];

    const { alerts, seen } = selectAlerts(items, null);

    expect(alerts).toEqual([]);
    expect([...seen]).toEqual(['a', 'b']);
  });

  it('announces one that arrived after the baseline', () => {
    const first = selectAlerts([notification('a', 'Error')], null);
    const second = selectAlerts([notification('b', 'Error'), notification('a', 'Error')], first.seen);

    expect(second.alerts.map((n) => n.id)).toEqual(['b']);
  });

  it('does not announce the same one twice', () => {
    const first = selectAlerts([], null);
    const second = selectAlerts([notification('a', 'Error')], first.seen);
    const third = selectAlerts([notification('a', 'Error')], second.seen);

    expect(second.alerts).toHaveLength(1);
    expect(third.alerts).toEqual([]);
  });

  it('leaves good news to the bell', () => {
    const first = selectAlerts([], null);
    const items = [
      notification('a', 'Info'),
      notification('b', 'Success'),
      notification('c', 'Warning'),
    ];

    expect(selectAlerts(items, first.seen).alerts.map((n) => n.id)).toEqual(['c']);
  });

  it('does not re-announce something the operator has already read', () => {
    const first = selectAlerts([], null);

    expect(selectAlerts([notification('a', 'Error', '2026-09-08T01:00:00Z')], first.seen).alerts)
      .toEqual([]);
  });

  /**
   * The bell fetches a page. A notification that scrolls off it and later comes back must not
   * become announceable again — which is why `seen` is a union rather than a replacement.
   */
  it('remembers one that scrolled off the page', () => {
    const first = selectAlerts([notification('a', 'Error')], null);
    const gone = selectAlerts([], first.seen);
    const back = selectAlerts([notification('a', 'Error')], gone.seen);

    expect(back.alerts).toEqual([]);
  });
});
