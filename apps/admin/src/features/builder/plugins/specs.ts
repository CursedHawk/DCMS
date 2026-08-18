import { pluginSpecs, type CustomFieldLike } from '@dcms/gjs-blocks';
import type { DcmsComponentSpec } from '@dcms/gjs-schema';
import { useMemo } from 'react';
import {
  customFieldsOf,
  parseInstanceConfig,
  usePluginCatalog,
  usePluginInstances,
  type PluginInstance,
  type PluginManifest,
} from '../../plugins/api';

/**
 * The tenant's plugin instances, as builder components.
 *
 * The generator lives in `@dcms/gjs-blocks` (and is tested there); this is the
 * admin-side join: two queries, plus the one thing the package cannot do for
 * itself — read tenant-defined custom fields out of an instance's config JSON.
 */
export function usePluginComponentSpecs(): {
  specs: DcmsComponentSpec[];
  enabledPluginIds: string[];
  isLoading: boolean;
} {
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();

  const enabledPluginIds = useMemo(
    () => [...new Set((instances.data ?? []).filter((i) => i.enabled).map((i) => i.pluginId))],
    [instances.data],
  );

  const specs = useMemo(
    () => buildSpecs(catalog.data ?? [], instances.data ?? []),
    [catalog.data, instances.data],
  );

  return { specs, enabledPluginIds, isLoading: catalog.isLoading || instances.isLoading };
}

/**
 * Exported for the same reason the generator is pure: this is the whole join, so
 * it can be reasoned about (and, if it grows, tested) without React.
 */
export function buildSpecs(
  catalog: readonly PluginManifest[],
  instances: readonly PluginInstance[],
): DcmsComponentSpec[] {
  const manifests = new Map(catalog.map((m) => [m.id, m]));
  const configs = new Map(instances.map((i) => [i.slug, parseInstanceConfig(i.config)]));
  const pluginOf = new Map(instances.map((i) => [i.slug, i.pluginId]));

  return pluginSpecs(catalog, instances, {
    customFields: (instanceSlug, contentTypeName): CustomFieldLike[] => {
      const manifest = manifests.get(pluginOf.get(instanceSlug) ?? '');
      const contentType = manifest?.contentTypes.find((c) => c.name === contentTypeName);
      const config = configs.get(instanceSlug);
      if (!contentType || !config) return [];
      return customFieldsOf(contentType, config);
    },
  });
}
