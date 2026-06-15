import type { SiteDefinition } from '@dcms/editor-core';
import { adminHeaders } from '../tenants';

const base = import.meta.env.VITE_ADMIN_API_BASE ?? '/api';

async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${base}${path}`, { ...init, headers: { ...(await adminHeaders()), ...init?.headers } });
  if (!res.ok) throw new Error(`${init?.method ?? 'GET'} ${path} → ${res.status}`);
  return (res.status === 204 ? undefined : await res.json()) as T;
}

export interface SiteSummary { id: string; name: string; renderMode: string; activeBuildId?: string }
export interface SiteDetail { id: string; name: string; renderMode: string; definition: SiteDefinition }
export interface PluginInstanceSummary { id: string; pluginId: string; slug: string; name: string; enabled: boolean }

export const sitesApi = {
  list: () => json<SiteSummary[]>('/admin/sites'),
  get: (id: string) => json<SiteDetail>(`/admin/sites/${id}`),
  create: (name: string) =>
    json<{ id: string }>('/admin/sites', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, renderMode: 'StaticPrerender' }),
    }),
  saveDefinition: (id: string, definition: SiteDefinition) =>
    json<void>(`/admin/sites/${id}/definition`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(definition),
    }),
  publish: (id: string) => json<{ buildId: string }>(`/admin/sites/${id}/publish`, { method: 'POST' }),
};

export const pluginsApi = {
  instances: () => json<PluginInstanceSummary[]>('/admin/plugins/instances'),
};
