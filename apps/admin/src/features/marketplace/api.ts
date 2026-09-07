import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api';
import { getCurrentTenantSlug } from '../../tenants';

export interface MarketplacePermission {
  key: string;
  displayName: string;
}

export interface MarketplaceItem {
  id: string;
  name: string;
  version: string;
  summary: string;
  description: string;
  category: string;
  tags: string[];
  icon: string | null;
  allowMultiple: boolean;
  permissions: MarketplacePermission[];
  contentTypes: string[];
  dependencies: { pluginId: string; optional: boolean }[];
  addsNavEntry: boolean;
  instanceCount: number;
  enabledCount: number;
  installed: boolean;
  /** "builtin" today. The field exists so a remote registry needs no SPA change. */
  source: string;
}

export function useMarketplace(enabled = true) {
  const tenant = getCurrentTenantSlug();
  return useQuery({
    queryKey: ['marketplace', tenant],
    enabled,
    staleTime: 5 * 60_000,
    queryFn: () => api.get<{ items: MarketplaceItem[] }>('/admin/marketplace'),
  });
}
