import type { TagQueryMap } from '@dcms/ui';

/**
 * What each server-side resource tag makes stale in this console.
 *
 * <p>These are key <b>prefixes</b>: react-query matches by prefix, so `['platform-tenants']`
 * covers every search variant of the list without the server knowing any of them.</p>
 *
 * <p>Three tags, because three things change in a way the platform can announce. Object-store
 * sizes and Loki's delete queue are read from systems that tell us nothing when they change,
 * and they keep the polls they already had — see `PlatformResourceTags` for why that is the
 * honest list rather than a shorter socket.</p>
 */
export const LIVE_QUERY_MAP: TagQueryMap = {
  tenants: [['platform-tenants'], ['platform-overview'], ['platform-growth']],
  certificates: [['platform-managed-certificates']],
  notifications: [['platform-notifications']],
};
