import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { RJSFSchema } from '@rjsf/utils';
import { api } from '../../lib/api';
import { getCurrentTenantSlug } from '../../tenants';

/*
 * A plugin's data sets (docs/adr/0018): tables of its data it describes and the console renders.
 * GET /admin/plugins/{slug}/_data lists the sets the caller may read, each with its schema;
 * everything else is addressed by set id, and rows by `?key=` because keys may contain '/'.
 */

export interface DataColumn {
  key: string;
  label: string;
  /** text | number | boolean | datetime | email | url | badge | bytes | json | media; unknown kinds render as text. */
  kind: string;
  sortable: boolean;
  primary: boolean;
}

export interface DataFilter {
  key: string;
  label: string;
  options: { value: string; label: string }[];
}

export interface DataAction {
  id: string;
  label: string;
  risk: 'read' | 'safe' | 'dangerous';
  bulk: boolean;
  description: string | null;
  inputSchema: RJSFSchema | null;
}

export interface DataSet {
  id: string;
  title: string;
  description: string | null;
  icon: string | null;
  /** Added by the platform for a store the plugin uses (dcms.storage, dcms.blobs). */
  platform: boolean;
  canWrite: boolean;
  columns: DataColumn[];
  itemSchema: RJSFSchema | null;
  filters: DataFilter[];
  actions: DataAction[];
  searchable: boolean;
  canCreate: boolean;
  canUpdate: boolean;
  canDelete: boolean;
  canDownload: boolean;
  defaultSort: string | null;
  defaultDescending: boolean;
}

export interface DataRow {
  key: string;
  values: Record<string, unknown>;
  title: string | null;
}

export interface DataPage {
  rows: DataRow[];
  total: number;
  page: number;
  pageSize: number;
}

export interface DataQuery {
  search: string;
  sort: string | null;
  descending: boolean;
  filters: Record<string, string>;
  page: number;
  pageSize: number;
}

const base = (slug: string, set?: string) =>
  `/admin/plugins/${encodeURIComponent(slug)}/_data${set ? `/${encodeURIComponent(set)}` : ''}`;

export function useDataSets(slug: string, enabled = true) {
  const tenant = getCurrentTenantSlug();
  return useQuery({
    queryKey: ['plugin-data', tenant, slug],
    enabled,
    queryFn: () => api.get<DataSet[]>(base(slug)),
  });
}

export function dataQueryString(q: DataQuery): string {
  const params = new URLSearchParams({ page: String(q.page), pageSize: String(q.pageSize) });
  if (q.search.trim()) params.set('search', q.search.trim());
  if (q.sort) {
    params.set('sort', q.sort);
    params.set('desc', String(q.descending));
  }
  for (const [key, value] of Object.entries(q.filters)) {
    if (value) params.set(`f.${key}`, value);
  }
  return params.toString();
}

export function useDataPage(slug: string, set: string, query: DataQuery) {
  const tenant = getCurrentTenantSlug();
  return useQuery({
    queryKey: ['plugin-data', tenant, slug, set, 'page', query],
    // Paging and filtering keep the last page on screen instead of flashing a spinner.
    placeholderData: keepPreviousData,
    queryFn: () => api.get<DataPage>(`${base(slug, set)}?${dataQueryString(query)}`),
  });
}

export function useDataRow(slug: string, set: string, key: string | null) {
  const tenant = getCurrentTenantSlug();
  return useQuery({
    queryKey: ['plugin-data', tenant, slug, set, 'row', key],
    enabled: key !== null,
    queryFn: () => api.get<DataRow>(`${base(slug, set)}/row?key=${encodeURIComponent(key!)}`),
  });
}

export const dataApi = {
  create: (slug: string, set: string, values: unknown) => api.post<DataRow>(`${base(slug, set)}/rows`, values),
  update: (slug: string, set: string, key: string, values: unknown) =>
    api.put<DataRow>(`${base(slug, set)}/row?key=${encodeURIComponent(key)}`, values),
  remove: (slug: string, set: string, key: string) => api.del(`${base(slug, set)}/row?key=${encodeURIComponent(key)}`),
  action: (slug: string, set: string, action: string, keys: string[], input?: unknown) =>
    api.post<{ affected: number; message: string | null }>(
      `${base(slug, set)}/actions/${encodeURIComponent(action)}`,
      { keys, input },
    ),
  /** Fetches the file with the member's credentials and saves it under its last path segment. */
  download: async (slug: string, set: string, key: string) => {
    const blob = await api.downloadBlob(`${base(slug, set)}/download?key=${encodeURIComponent(key)}`);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = key.split('/').pop() || 'download';
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
  },
};
