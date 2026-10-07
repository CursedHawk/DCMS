import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import { getCurrentTenantSlug } from '../tenants';

export interface PluginScreenInfo {
  id: string;
  title: string;
  titles: Record<string, string> | null;
  description: string | null;
  scope: 'plugin' | 'instance';
  icon: string | null;
  permission: string;
  nav: { group: string; order: number } | null;
  /** Whether the signed-in member may open it. */
  allowed: boolean;
}

/** Where a plugin's admin UI module comes from: compiled into the console, or an installed plugin's folder. */
export type PluginModuleRef =
  | { kind: 'builtin'; key: string }
  | { kind: 'url'; url: string; css: string | null };

export interface PluginUiInfo {
  pluginId: string;
  name: string;
  icon: string | null;
  source: string;
  module: PluginModuleRef | null;
  screens: PluginScreenInfo[];
  instances: { id: string; slug: string; name: string; enabled: boolean; aiToolsEnabled?: boolean }[];
}

/** GET /admin/plugin-ui: every plugin's screens, UI module and instances (docs/adr/0019). */
export function usePluginUi() {
  const tenant = getCurrentTenantSlug();
  return useQuery({
    queryKey: ['plugin-ui', tenant],
    staleTime: 60_000,
    queryFn: () => api.get<PluginUiInfo[]>('/admin/plugin-ui'),
  });
}

/** A screen's title in the reader's language, falling back to the plugin's English. */
export function screenTitle(screen: Pick<PluginScreenInfo, 'title' | 'titles'>, language: string): string {
  return screen.titles?.[language] ?? screen.titles?.[language.split('-')[0]] ?? screen.title;
}
