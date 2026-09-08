import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TooltipProvider } from '../ui/tooltip';
import { user } from '../test/user';
import { NotificationBell, NotificationRow, type BellNotification } from './NotificationBell';

/*
 * The bell's trigger and its rows are tested; opening the popover is not. See `test/user.ts`
 * for why — jsdom cannot lay out a floating overlay, so the test would be both very slow and
 * unable to check the only thing a popover really risks getting wrong. Playwright covers the
 * open-and-click path.
 */

const labels = {
  title: 'Notifications',
  markAllRead: 'Mark all read',
  empty: 'Nothing to report',
  viewAll: 'View all',
  dismiss: 'Dismiss',
  loading: 'Loading…',
  unread: (n: number) => `Notifications, ${n} unread`,
};

const items: BellNotification[] = [
  { id: '1', severity: 'Error', createdAt: '2026-09-07T10:00:00Z', readAt: null },
  { id: '2', severity: 'Success', createdAt: '2026-09-06T10:00:00Z', readAt: '2026-09-06T11:00:00Z' },
];

/*
 * The clock is pinned, because the fixtures above are fixed dates and the assertions are about
 * how a time is FORMATTED rather than about what today is.
 *
 * Without this the relative-time test passed for one day and then began failing on its own:
 * `Intl.RelativeTimeFormat` with `numeric: 'auto'` says "1 hour ago" at an hour's distance and
 * "yesterday" at a day's, so the assertion that a row renders something like "ago" quietly
 * became false at midnight — a test that fails for a reason that has nothing to do with the
 * code it covers, on a day nobody touched it.
 */
beforeAll(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(new Date('2026-09-07T12:00:00Z'));
});

afterAll(() => vi.useRealTimers());

function Bell(props: Partial<React.ComponentProps<typeof NotificationBell>> = {}) {
  return (
    <TooltipProvider>
      <NotificationBell
        items={items}
        unreadCount={1}
        labels={labels}
        renderItem={(n) => ({ title: `Item ${n.id}`, body: `Body ${n.id}` })}
        onOpenItem={() => {}}
        onDismiss={() => {}}
        onMarkAllRead={() => {}}
        {...props}
      />
    </TooltipProvider>
  );
}

describe('NotificationBell', () => {
  it('renders nothing at all when disabled', () => {
    // The platform console passes `enabled={isSuperAdmin}`; a bell that renders and then 403s
    // on every poll is worse than no bell.
    const { container } = render(<Bell enabled={false} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('puts the unread count in the accessible name, not only in a coloured dot', () => {
    render(<Bell unreadCount={3} />);
    expect(screen.getByRole('button', { name: 'Notifications, 3 unread' })).toBeInTheDocument();
  });

  it('drops the count from the name when there is nothing unread', () => {
    render(<Bell unreadCount={0} />);
    expect(screen.getByRole('button', { name: 'Notifications' })).toBeInTheDocument();
  });

  it('caps the badge so a large number cannot widen the top bar', () => {
    render(<Bell unreadCount={250} />);
    expect(screen.getByText('99+')).toBeInTheDocument();
  });

  it('shows no badge at zero', () => {
    render(<Bell unreadCount={0} />);
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });

  it('keeps the panel shut until asked — a glance must not mark anything read', () => {
    render(<Bell />);
    expect(screen.queryByText('Item 1')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Notifications/ })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
  });
});

function Row(props: Partial<React.ComponentProps<typeof NotificationRow>> = {}) {
  return (
    <NotificationRow
      item={items[0]}
      text={{ title: 'Site deployed', body: 'marketing-site is live' }}
      dismissLabel="Dismiss"
      onOpen={() => {}}
      onDismiss={() => {}}
      {...props}
    />
  );
}

describe('NotificationRow', () => {
  it('shows the title and body the app rendered', () => {
    render(<Row />);
    expect(screen.getByText('Site deployed')).toBeInTheDocument();
    expect(screen.getByText('marketing-site is live')).toBeInTheDocument();
  });

  it('omits an empty body rather than rendering a blank line', () => {
    render(<Row text={{ title: 'Site deployed' }} />);
    expect(screen.getByText('Site deployed')).toBeInTheDocument();
    expect(screen.queryByText('marketing-site is live')).not.toBeInTheDocument();
  });

  it('renders a relative time, not a raw timestamp', () => {
    render(<Row />);
    expect(screen.queryByText(/2026-09-07T10:00:00Z/)).not.toBeInTheDocument();
    expect(screen.getByText(/ago|now/i)).toBeInTheDocument();
  });

  it('marks an unread row visually', () => {
    const { container } = render(<Row item={{ ...items[0], readAt: null }} />);
    expect(container.firstElementChild?.className).toContain('bg-primary/[0.04]');
  });

  it('does not mark a read row', () => {
    const { container } = render(<Row item={items[1]} />);
    expect(container.firstElementChild?.className).not.toContain('bg-primary/[0.04]');
  });

  it('opens on the body, so the whole row is the target', async () => {
    const onOpen = vi.fn();
    render(<Row onOpen={onOpen} />);
    await user.click(screen.getByText('Site deployed'));
    expect(onOpen).toHaveBeenCalledOnce();
  });

  it('dismisses without opening — the X is not a click-through', async () => {
    const onDismiss = vi.fn();
    const onOpen = vi.fn();
    render(<Row onDismiss={onDismiss} onOpen={onOpen} />);
    await user.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(onDismiss).toHaveBeenCalledOnce();
    expect(onOpen).not.toHaveBeenCalled();
  });

  it('gives the dismiss control a name — it is an icon with no text', () => {
    render(<Row dismissLabel="Zahodit" />);
    expect(screen.getByRole('button', { name: 'Zahodit' })).toBeInTheDocument();
  });

  it('falls back to the info icon for a severity it does not know', () => {
    // A console can be older than the server it talks to; an unknown severity should degrade
    // to a neutral row rather than crash the bell.
    const odd = { ...items[0], severity: 'Catastrophe' as unknown as BellNotification['severity'] };
    expect(() => render(<Row item={odd} />)).not.toThrow();
    expect(screen.getByText('Site deployed')).toBeInTheDocument();
  });
});
