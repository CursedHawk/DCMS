import { useQuery } from '@tanstack/react-query';
import { platformApi } from '../../lib/api';

export interface OverviewTotals {
  tenants: number;
  activeTenants: number;
  suspendedTenants: number;
  sites: number;
  contentItems: number;
  publishedItems: number;
  mediaAssets: number;
  storageBytes: number;
  members: number;
}

export interface GrowthPoint {
  day: string;
  newTenants: number;
  newUsers: number;
  newSites: number;
  newContent: number;
}

export function useOverview() {
  return useQuery({
    queryKey: ['platform-overview'],
    queryFn: () => platformApi.get<OverviewTotals>('/overview'),
    // The overview is what someone leaves open on a second monitor during an incident, so it
    // has to stay fresh on its own. It no longer does that by asking: these totals move on the
    // tenant plane, where this API hears nothing, so platform-api samples them on a timer and
    // pushes the `overview` tag — one timer per replica instead of one per second monitor.
  });
}

export function useGrowth(days: number) {
  return useQuery({
    queryKey: ['platform-growth', days],
    queryFn: () => platformApi.get<GrowthPoint[]>(`/growth?days=${days}`),
    staleTime: 5 * 60_000,
  });
}
