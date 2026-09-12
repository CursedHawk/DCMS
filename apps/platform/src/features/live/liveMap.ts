import type { TagQueryMap } from '@dcms/ui';

/**
 * What each server-side resource tag makes stale in this console.
 *
 * <p>These are key <b>prefixes</b>: react-query matches by prefix, so `['platform-tenants']`
 * covers every search variant of the list without the server knowing any of them.</p>
 *
 * <p>Six tags, of two kinds. `tenants`, `certificates` and `notifications` are announcements:
 * something happened and the platform said so. `health`, `stores` and `overview` are sample
 * ticks — the data behind them is read out of Prometheus, Loki and the tenant plane, none of
 * which announce anything, so platform-api runs their period on a timer and pushes the tick.
 * Those used to be timers in this file's neighbours; the difference is that the
 * period is now paid once per replica rather than once per open tab.</p>
 */
export const LIVE_QUERY_MAP: TagQueryMap = {
  tenants: [['platform-tenants'], ['platform-overview'], ['platform-growth']],
  certificates: [['platform-managed-certificates']],
  notifications: [['platform-notifications']],
  health: [['platform-health-signals']],
  stores: [['platform-stores'], ['platform-loki-purges']],
  overview: [['platform-overview']],
};

/**
 * The floor between two handled ticks of a sample tag.
 *
 * <p>A sample tick is broadcast by every replica holding a console connection, so a scaled
 * platform-api multiplies them and every duplicate costs each open console a refetch. Twenty
 * seconds under the server's thirty absorbs that without ever delaying a legitimate tick.</p>
 *
 * <p>The announcement tags take no entry on purpose: dropping a real change because a similar
 * one arrived recently is the staleness the push exists to prevent.</p>
 */
export const LIVE_THROTTLE_MS: Readonly<Record<string, number>> = {
  health: 20_000,
  stores: 20_000,
  overview: 20_000,
};

/**
 * Every key prefix the socket is responsible for, flattened.
 *
 * <p>This is what a missed-message window costs, so it is what gets refetched when one closes —
 * see `useHubRevalidation`. Derived from the map rather than written twice, so a tag added above
 * cannot be forgotten here.</p>
 *
 * <p>Invalidating the whole list at once is cheap: react-query only refetches queries that are
 * currently mounted and marks the rest stale, so this costs the open page and nothing else.</p>
 */
export const LIVE_KEYS: readonly (readonly unknown[])[] = Object.values(LIVE_QUERY_MAP).flat();
