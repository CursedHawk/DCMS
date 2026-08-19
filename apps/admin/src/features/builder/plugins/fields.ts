import { contentFields, type ContentField } from '@dcms/gjs-blocks';
import { useMemo } from 'react';
import {
  customFieldsOf,
  parseInstanceConfig,
  usePluginCatalog,
  usePluginInstances,
} from '../../plugins/api';

/**
 * The admin-side join for the component builder's field list.
 *
 * The package can list a content type's fields; only the admin can read the
 * tenant-defined ones, because they live in the plugin *instance's* config JSON.
 * That is the same join `usePluginComponentSpecs` does for the generated blocks,
 * kept separate because what it produces is different: a spec's field mappings
 * are a fixed set of dropdowns, and this is the raw list a binding UI shows.
 */

export interface ContentSource {
  instanceSlug: string;
  instanceName: string;
  contentType: string;
  /** Content types without one cannot be shown as a detail view. */
  hasSlug: boolean;
}

/** Every instance/content-type pair a component could be bound to. */
export function useContentSources(): { sources: ContentSource[]; isLoading: boolean } {
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();

  const sources = useMemo(() => {
    const manifests = new Map((catalog.data ?? []).map((m) => [m.id, m]));
    const found: ContentSource[] = [];
    for (const instance of instances.data ?? []) {
      if (!instance.enabled) continue;
      const manifest = manifests.get(instance.pluginId);
      for (const contentType of manifest?.contentTypes ?? []) {
        found.push({
          instanceSlug: instance.slug,
          instanceName: instance.name,
          contentType: contentType.name,
          hasSlug: Boolean(contentType.slugField),
        });
      }
    }
    return found;
  }, [catalog.data, instances.data]);

  return { sources, isLoading: catalog.isLoading || instances.isLoading };
}

/**
 * The fields of one instance's content type, the plugin's and the tenant's.
 *
 * Returns an empty list rather than throwing for an unknown source: a definition
 * can name an instance that was since deleted, and the component builder should
 * open on it and say so, not fail to render.
 */
export function useContentFields(
  instanceSlug: string | undefined,
  contentTypeName: string | undefined,
): ContentField[] {
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();

  return useMemo(() => {
    if (!instanceSlug || !contentTypeName) return [];
    const instance = (instances.data ?? []).find((i) => i.slug === instanceSlug);
    const manifest = (catalog.data ?? []).find((m) => m.id === instance?.pluginId);
    const contentType = manifest?.contentTypes.find((c) => c.name === contentTypeName);
    if (!instance || !contentType) return [];

    return contentFields(contentType, customFieldsOf(contentType, parseInstanceConfig(instance.config)));
  }, [catalog.data, instances.data, instanceSlug, contentTypeName]);
}
