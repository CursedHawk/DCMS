import { META_FIELDS, contentFields, type ContentField } from '@dcms/gjs-blocks';
import { sourceSchema, sourceKey, type ContentItem, type ContentSchema, type DataClient, type Source } from '@dcms/site-runtime';
import type { Component } from 'grapesjs';
import { useMemo } from 'react';
import { api } from '../../lib/api';
import { customFieldsOf, parseInstanceConfig, usePluginCatalog, usePluginInstances } from '../plugins/api';
import { ID, PROPS, SLOT } from './canvas/tree';

/**
 * Content for the builder (P4): the canvas and the preview read the tenant's real published
 * content through admin-api's preview proxy (`/api/admin/sites/{id}/preview/api/*` — the same
 * delivery API the site calls, with the admin's credentials and sandboxed writes).
 */
export function previewClient(siteId: string): DataClient {
  const base = `/admin/sites/${siteId}/preview`;
  return {
    get: (path) => api.get<unknown>(`${base}${path}`),
    post: (path, body) => api.post<unknown>(`${base}${path}`, body),
  };
}

/** Every plugin instance's content types and their fields — what the field picker offers. */
export interface ContentCatalog {
  sources: { source: Source; label: string; fields: ContentField[] }[];
  /** For the validator: `instance/contentType` → field paths. */
  schema: ContentSchema;
  isLoading: boolean;
}

export function useContentCatalog(): ContentCatalog {
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();
  return useMemo(() => {
    const manifests = new Map((catalog.data ?? []).map((m) => [m.id, m]));
    const sources: ContentCatalog['sources'] = [];
    for (const instance of instances.data ?? []) {
      if (!instance.enabled) continue;
      const manifest = manifests.get(instance.pluginId);
      for (const contentType of manifest?.contentTypes ?? []) {
        const source = sourceSchema.safeParse({ instance: instance.slug, contentType: contentType.name });
        if (!source.success) continue;
        // A JSON list of image ids (Events' photos, a gallery's images) is an image to Mode D:
        // the runtime shows its first. Mode A's picker keeps calling it text — its runtime cannot.
        const images = new Set(contentType.fields.filter((f) => f.reference?.mediaCategory === 'Image').map((f) => f.name));
        const fields = contentFields(contentType, customFieldsOf(contentType, parseInstanceConfig(instance.config))).map((f) =>
          images.has(f.path) ? { ...f, kind: 'media' as const } : f,
        );
        sources.push({ source: source.data, label: `${instance.name} › ${contentType.name}`, fields });
      }
    }
    const schema = new Map(sources.map((s) => [sourceKey(s.source), new Set(s.fields.map((f) => f.path))]));
    return { sources, schema, isLoading: catalog.isLoading || instances.isLoading };
  }, [catalog.data, catalog.isLoading, instances.data, instances.isLoading]);
}

/** The meta fields the runtime understands (`#slug`, `#index`…), out of the list the admin keeps. */
const RUNTIME_META = new Set(['#slug', '#id', '#publishedAt', '#index', '#number', '#count']);
export const META_BINDABLE: ContentField[] = META_FIELDS.filter((f) => RUNTIME_META.has(f.path));

/**
 * Where a node on the canvas gets its item: the nearest collection whose item template it is
 * in, or — on a detail page — the page. GrapesJS's own tree answers this; React context cannot,
 * because every node on the canvas is its own React root.
 */
export type CanvasScope = { kind: 'collection'; nodeId: string; source: Source | null } | { kind: 'page' } | null;

export function scopeOf(model: Component, pageHasData: boolean): CanvasScope {
  for (let parent = model.parent(); parent; parent = parent.parent()) {
    // A node's parent is a slot; the slot's parent is the component that owns it.
    if (parent.get('type') === 'dcms-slot' && parent.get(SLOT) === 'item') {
      const owner = parent.parent();
      if (owner?.get('type') === 'dcms.collection') {
        const source = sourceSchema.safeParse(((owner.get(PROPS) ?? {}) as Record<string, unknown>).source);
        return { kind: 'collection', nodeId: owner.get(ID) as string, source: source.success ? source.data : null };
      }
    }
  }
  return pageHasData ? { kind: 'page' } : null;
}

export type { ContentItem };
