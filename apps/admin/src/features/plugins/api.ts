import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api';

export type FieldType =
  | 'Text'
  | 'RichText'
  | 'Markdown'
  | 'Number'
  | 'Boolean'
  | 'DateTime'
  | 'Json'
  | 'MediaRef'
  | 'ContentRef'
  | 'Tags';

export interface ContentFieldDef {
  name: string;
  type: FieldType;
  required: boolean;
  description?: string;
  reference?: {
    targetPluginId?: string;
    contentType?: string;
    mediaCategory?: string;
  };
}

/**
 * A content type whose fields are not all fixed by the plugin: `configKey` names
 * the instance-config array holding the tenant's own field definitions, and
 * `valuesField` the item field their values are nested under.
 */
export interface CustomFieldsDef {
  valuesField: string;
  configKey: string;
}

/** One tenant-defined field, as authored in a plugin instance's config. */
export interface CustomFieldDef {
  key: string;
  label: string;
  type: 'text' | 'longText' | 'number' | 'boolean' | 'date' | 'tags' | 'url' | 'image';
  description?: string;
  required?: boolean;
}

export interface ContentTypeDef {
  name: string;
  searchable: boolean;
  slugField?: string;
  fields: ContentFieldDef[];
  customFields?: CustomFieldsDef | null;
}

export interface PluginManifest {
  id: string;
  name: string;
  version: string;
  description: string;
  allowMultipleInstances: boolean;
  configJsonSchema: string;
  permissions: { action: string; displayName: string }[];
  dependencies: { pluginId: string; optional: boolean }[];
  publicConfigKeys: string[];
  contentTypes: ContentTypeDef[];
}

export interface PluginInstance {
  id: string;
  pluginId: string;
  slug: string;
  name: string;
  description: string;
  enabled: boolean;
  /** Instance configuration as a JSON string, shaped by the manifest schema. */
  config: string;
}

export function usePluginCatalog() {
  return useQuery({
    queryKey: ['plugin-catalog'],
    staleTime: 5 * 60_000,
    queryFn: () => api.get<PluginManifest[]>('/admin/plugins/catalog'),
  });
}

export function usePluginInstances() {
  return useQuery({
    queryKey: ['plugin-instances'],
    queryFn: () => api.get<PluginInstance[]>('/admin/plugins/instances'),
  });
}

/** Parse a manifest's config JSON Schema string into an object for RJSF. */
export function parseConfigSchema(json: string): Record<string, unknown> {
  try {
    return JSON.parse(json || '{}');
  } catch {
    return {};
  }
}

/** Parse an instance's stored config. Same shape as the schema above, so same guard. */
export function parseInstanceConfig(json: string | undefined): Record<string, unknown> {
  return parseConfigSchema(json ?? '{}');
}

/**
 * The tenant-defined fields on an instance, for a content type that declares it
 * has any. Unusable entries are dropped rather than rendered as broken inputs —
 * the config is hand-editable, so a half-finished row is expected.
 */
export function customFieldsOf(
  contentType: ContentTypeDef,
  instanceConfig: Record<string, unknown>,
): CustomFieldDef[] {
  if (!contentType.customFields) return [];
  const raw = instanceConfig[contentType.customFields.configKey];
  if (!Array.isArray(raw)) return [];
  return raw
    .filter((f): f is CustomFieldDef => !!f && typeof f === 'object' && typeof (f as CustomFieldDef).key === 'string')
    .map((f) => ({ ...f, label: f.label || f.key, type: f.type ?? 'text' }));
}
