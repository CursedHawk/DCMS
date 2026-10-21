import { META_FIELDS, contentFields, type ContentField } from '@dcms/gjs-blocks';
import { sourceSchema, sourceKey, visitorToken, type ContentItem, type ContentSchema, type DataClient, type Source } from '@dcms/site-runtime';
import type { Component } from 'grapesjs';
import { useQueries, useQuery } from '@tanstack/react-query';
import { useParams } from '@tanstack/react-router';
import { useMemo } from 'react';
import { api } from '../../lib/api';
import { CONNECTIONS_KEY, type ApiConnection } from '../connections/api';
import { customFieldsOf, parseInstanceConfig, usePluginCatalog, usePluginInstances } from '../plugins/api';
import { ID, PROPS, SLOT } from './canvas/tree';

/**
 * Content for the builder (P4): the canvas and the preview read the tenant's real published
 * content through admin-api's preview proxy (`/api/admin/sites/{id}/preview/api/*` — the same
 * delivery API the site calls, with the admin's credentials and sandboxed writes).
 */
export function previewClient(siteId: string): DataClient {
  const base = `/admin/sites/${siteId}/preview`;
  // The visitor the preview signed in (Visitor Authentication, in the sandbox) goes along in a
  // header of its own: Authorization here is the console's.
  const visitor = async () => {
    const token = await visitorToken((instance, refreshToken) => api.post(`${base}/api/${instance}/refresh`, { refreshToken }));
    return token ? { headers: { 'X-Dcms-Visitor-Token': token } } : undefined;
  };
  return {
    get: async (path) => api.get<unknown>(`${base}${path}`, await visitor()),
    // One key per write, as the site's own client sends (ADR 0024): content-api refuses a POST without one.
    post: async (path, body) => {
      const opts = await visitor();
      return api.post<unknown>(`${base}${path}`, body, { headers: { ...opts?.headers, 'Idempotency-Key': crypto.randomUUID() } });
    },
  };
}

/**
 * What an external API's field probably holds, from its name: an image, a link, a date — so it
 * can bind to an Image or a Link. ponytail: a name heuristic; the sync could record value kinds
 * if names prove too unreliable.
 */
function guessKind(path: string): ContentField['kind'] {
  const name = path.split('.').pop()!.toLowerCase();
  if (/image|photo|picture|thumbnail|cover|logo|avatar/.test(name)) return 'media';
  if (/url|link|href/.test(name)) return 'url';
  if (/date|time|_at$|at$/.test(name)) return 'date';
  return 'text';
}

/** A table of a Dynamic Apps instance's public model, as the site's `GET /api/{app}/_model` sends it. */
export interface AppModelTable {
  apiName: string;
  displayName: string;
  pluralName?: string | null;
  primaryField?: string | null;
  access?: { read: string; create: boolean; updateOwn: boolean; deleteOwn: boolean; rowRules: boolean };
  fields: { apiName: string; displayName: string; type: string; required?: boolean; readOnly?: boolean; target?: string | null; options?: string[] | null }[];
  relationships?: { apiName: string; kind: string; target: string }[];
}

export const DYNAMIC_APPS = 'dynamic-apps';

/** What a Dynamic Apps field holds, in the field picker's terms. */
function appFieldKind(type: string): ContentField['kind'] {
  switch (type) {
    case 'integer':
    case 'decimal':
      return 'number';
    case 'boolean':
      return 'boolean';
    case 'date':
    case 'dateTime':
      return 'date';
    case 'url':
      return 'url';
    case 'media':
      return 'media';
    case 'multiChoice':
      return 'tags';
    case 'longText':
      return 'richText';
    default:
      return 'text';
  }
}

/**
 * Each Dynamic Apps instance's public model, read the way the site reads it — through the site's
 * preview proxy, so it is exactly what the site may read, and needs no plugin permission beyond
 * opening the site. A failure is an app with no tables, not an error: an app that has published
 * nothing has nothing for a site to show.
 */
export function useAppModels(siteId: string | undefined, slugs: readonly string[]) {
  return useQueries({
    queries: slugs.map((slug) => ({
      queryKey: ['app-public-model', siteId, slug],
      enabled: !!siteId,
      retry: false,
      staleTime: 60_000,
      queryFn: async () => ((await previewClient(siteId!).get(`/api/${slug}/_model`)) as { tables?: AppModelTable[] })?.tables ?? [],
    })),
  });
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
  const connections = useQuery({ queryKey: CONNECTIONS_KEY, queryFn: () => api.get<ApiConnection[]>('/admin/connections'), retry: false });
  const { siteId } = useParams({ strict: false }) as { siteId?: string };
  const apps = (instances.data ?? []).filter((i) => i.enabled && i.pluginId === DYNAMIC_APPS).map((i) => i.slug);
  const models = useAppModels(siteId, apps);
  const modelData = models.map((m) => m.data);
  const modelsLoading = models.some((m) => m.isLoading);
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
        // the runtime shows its first. So is a text field holding an image's URL that the plugin
        // marks the same way (YouTube's thumbnail). Mode A's picker keeps calling them text.
        const images = new Set(contentType.fields.filter((f) => f.reference?.mediaCategory === 'Image').map((f) => f.name));
        const fields = contentFields(contentType, customFieldsOf(contentType, parseInstanceConfig(instance.config))).map((f) =>
          images.has(f.path) ? { ...f, kind: 'media' as const } : f,
        );
        sources.push({ source: source.data, label: `${instance.name} › ${contentType.name}`, fields });
      }
    }
    // External API connections' operations: their fields as the last sync found them. Any
    // field binds (`*`) — an API's items are whatever it returns, and the sync may not have seen
    // every field yet.
    const connectionKeys = new Set<string>();
    for (const connection of connections.data ?? []) {
      for (const shape of connection.shapes ?? []) {
        const source = sourceSchema.safeParse({ connection: connection.slug, operation: shape.operation, ...(shape.items ? { items: shape.items } : {}) });
        if (!source.success) continue;
        connectionKeys.add(sourceKey(source.data));
        sources.push({
          source: source.data,
          label: `${connection.name} › GET ${shape.operation}`,
          fields: shape.fields.map((path) => ({ path, label: path, kind: guessKind(path), custom: false })),
        });
      }
    }
    // Dynamic Apps tables the site may read: records, by the app's public API. A lookup is an id
    // unless the source expands it, so its target's fields are offered as `account.name`, and the
    // validator is told (`lookup:account`) to want the expand.
    const lookups = new Map<string, string[]>();
    const readable = (t: AppModelTable) => !(t.access && t.access.read === 'none' && !t.access.rowRules);
    apps.forEach((app, index) => {
      const tables = modelData[index] ?? [];
      // A table's fields, and its lookups' target fields as `account.name`.
      const fieldsOf = (table: AppModelTable): { fields: ContentField[]; markers: string[] } => {
        const fields: ContentField[] = [
          ...table.fields.map((f) => ({ path: f.apiName, label: f.displayName, kind: appFieldKind(f.type), custom: false })),
          { path: 'created_at', label: 'Created', kind: 'date' as const, custom: false },
          { path: 'updated_at', label: 'Updated', kind: 'date' as const, custom: false },
        ];
        const markers: string[] = [];
        for (const lookup of table.fields.filter((f) => f.type === 'lookup' && f.target)) {
          const target = tables.find((t) => t.apiName === lookup.target);
          markers.push(`lookup:${lookup.apiName}`);
          for (const f of target?.fields ?? []) {
            if (f.type === 'lookup') continue;
            fields.push({ path: `${lookup.apiName}.${f.apiName}`, label: `${lookup.displayName} › ${f.displayName}`, kind: appFieldKind(f.type), custom: false });
          }
        }
        return { fields, markers };
      };
      for (const table of tables) {
        if (!readable(table)) continue;
        // Picked from the list, a table comes with its lookups expanded, so `account.name` reads.
        const expand = table.fields.filter((f) => f.type === 'lookup').map((f) => f.apiName).slice(0, 10);
        const source = sourceSchema.safeParse({ app, table: table.apiName, ...(expand.length ? { expand } : {}) });
        if (!source.success) continue;
        const { fields, markers } = fieldsOf(table);
        lookups.set(sourceKey(source.data), markers);
        sources.push({ source: source.data, label: `${app} › ${table.pluralName ?? table.displayName}`, fields });
        // Its relationships: the records related to one of its records (an account's deals).
        for (const relationship of table.relationships ?? []) {
          const target = tables.find((t) => t.apiName === relationship.target);
          const related = sourceSchema.safeParse({ app, table: table.apiName, related: relationship.apiName });
          if (!target || !readable(target) || !related.success) continue;
          const shape = fieldsOf(target);
          lookups.set(sourceKey(related.data), shape.markers);
          sources.push({ source: related.data, label: `${app} › ${table.displayName} › ${relationship.apiName}`, fields: shape.fields });
        }
      }
    });
    const schema = new Map(
      sources.map((s) => {
        const key = sourceKey(s.source);
        return [key, new Set([...s.fields.map((f) => f.path), ...(connectionKeys.has(key) ? ['*'] : []), ...(lookups.get(key) ?? [])])];
      }),
    );
    return { sources, schema, isLoading: catalog.isLoading || instances.isLoading || connections.isLoading || modelsLoading };
    // `modelData` is a new array each render; its JSON is what changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [catalog.data, catalog.isLoading, instances.data, instances.isLoading, connections.data, connections.isLoading, JSON.stringify(modelData), modelsLoading]);
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
