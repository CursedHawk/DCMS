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
  /** Contract ids this plugin offers other plugins. */
  provides?: string[];
  /** Contracts this plugin uses; platform ones (`dcms.*`) are always available. */
  consumes?: { contractId: string; optional: boolean }[];
  addsNavEntry: boolean;
  instanceCount: number;
  enabledCount: number;
  installed: boolean;
  /** "builtin" (compiled in) or "installed" (the operator's plugin directory). */
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

/* ---- The developer reference (GET /admin/marketplace/{id}/reference) ---- */

export interface ReferenceOperation {
  name: string;
  method: string;
  risk: 'read' | 'safe' | 'dangerous';
  permission: string | null;
  /** Always "plugins"; plus "site", "admin", "ai" where the operation is exposed. */
  exposed: string[];
  returnsExternalText: boolean;
  description: string | null;
  inputType: string | null;
  outputType: string;
  inputSchema: unknown;
  outputSchema: unknown;
}

export interface ReferenceSchema {
  name: string;
  clrType: string;
  schema: unknown;
}

export interface ReferenceContract {
  id: string;
  description: string | null;
  clrType: string;
  assembly: string;
  providers: string[];
  operations: ReferenceOperation[];
  events: ReferenceSchema[];
  hooks: ReferenceSchema[];
}

export interface PluginReference {
  id: string;
  name: string;
  version: string;
  description: string;
  source: string;
  sdkMajor: number;
  packages: { id: string; purpose: string }[];
  provides: ReferenceContract[];
  consumes: { contractId: string; optional: boolean; bindingConfigKey: string | null; providers: string[] }[];
  subscribes: string[];
  intercepts: { hook: string; priority: number }[];
  jobs: { name: string; intervalMinutes: number | null }[];
  contentTypes: {
    name: string;
    searchable: boolean;
    fields: { name: string; type: string; required: boolean; description: string | null }[];
    publishedEvent: string | null;
    unpublishedEvent: string | null;
  }[];
  config: unknown;
  publicConfigKeys: string[];
  permissions: { key: string; displayName: string }[];
  /** Tables of the plugin's data shown on each instance's page. */
  dataSets?: { id: string; title: string; description: string | null; readPermission: string; writePermission: string }[];
  cSharp: string;
}

export function usePluginReference(pluginId: string) {
  return useQuery({
    queryKey: ['plugin-reference', pluginId],
    enabled: pluginId.length > 0,
    staleTime: 5 * 60_000,
    queryFn: () => api.get<PluginReference>(`/admin/marketplace/${encodeURIComponent(pluginId)}/reference`),
  });
}

/** The admin-plane catalog: which instances serve each contract, for running operations. */
export interface AdminCatalogContract {
  id: string;
  instances: { id: string; slug: string; name: string; pluginId: string }[];
  operations: { name: string }[];
}

export function useAdminContractCatalog() {
  const tenant = getCurrentTenantSlug();
  return useQuery({
    queryKey: ['contracts', 'admin', tenant],
    staleTime: 60_000,
    queryFn: () => api.get<AdminCatalogContract[]>('/admin/contracts?plane=admin'),
  });
}

export function runContractOperation(contractId: string, operation: string, instance: string, input: unknown) {
  const params = new URLSearchParams({ plane: 'admin', instance });
  return api.post<unknown>(
    `/admin/contracts/${encodeURIComponent(contractId)}/${encodeURIComponent(operation)}?${params}`,
    input,
  );
}
