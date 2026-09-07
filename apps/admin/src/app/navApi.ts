import { useQuery } from '@tanstack/react-query';
import * as icons from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { Plug } from 'lucide-react';
import { api } from '../lib/api';
import { getCurrentTenantSlug } from '../tenants';

export interface NavResponseItem {
  to: string;
  labelKey: string;
  /** A lucide icon name. Unknown names fall back to the plugin glyph. */
  icon: string;
  group: string;
  /**
   * A tenant-authored name — an instance called "Press kit". Present only for plugin entries,
   * and deliberately NOT run through i18n: there is no key for a name somebody typed.
   */
  label: string | null;
}

/**
 * The menu, from the server.
 *
 * <p>It used to be a hard-coded array of sixteen destinations filtered by permission, which
 * cannot answer the question a plugin system creates: a workspace with two Image Gallery
 * instances and no Forms instance should see two gallery entries and no forms entry, and
 * nothing shipped in this bundle knows either fact.</p>
 *
 * <p>Keyed by tenant, because that is exactly what it varies by. `staleTime` is generous and
 * the `plugins` resource tag invalidates it — enabling a plugin changes the menu, and that is
 * the one moment it needs to be immediate.</p>
 */
export function useNavigation(enabled: boolean) {
  const tenant = getCurrentTenantSlug();
  return useQuery({
    queryKey: ['navigation', tenant],
    enabled,
    staleTime: 5 * 60_000,
    queryFn: () => api.get<{ items: NavResponseItem[] }>('/admin/navigation'),
  });
}

/**
 * Resolves a lucide icon name sent by the server.
 *
 * The server names icons rather than sending markup, so a console that does not recognise one
 * shows a generic glyph instead of an empty space. That is the right failure for a client that
 * may be older than the server it is talking to — a new plugin's entry still appears, still
 * navigates, and merely looks generic until the SPA catches up.
 */
export function iconByName(name: string | null | undefined): LucideIcon {
  if (!name) return Plug;
  const found = (icons as unknown as Record<string, unknown>)[name];
  return typeof found === 'function' || typeof found === 'object' ? (found as LucideIcon) : Plug;
}
