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

export interface ContentTypeDef {
  name: string;
  searchable: boolean;
  slugField?: string;
  fields: ContentFieldDef[];
}

export interface PluginManifest {
  id: string;
  name: string;
  version: string;
  description: string;
  allowMultipleInstances: boolean;
  configJsonSchema: string;
  permissions: { action: string; displayName: string }[];
  contentTypes: ContentTypeDef[];
}

export interface PluginInstance {
  id: string;
  pluginId: string;
  slug: string;
  name: string;
  description: string;
  enabled: boolean;
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
