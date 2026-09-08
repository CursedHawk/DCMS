import { can, type MyPermissions } from '@dcms/core';
import { Perm } from '../../lib/permissions';
import { api } from '../../lib/api';

/**
 * How far into the workspace the assistant may reach.
 *
 * <p><b>read</b> is the default and the safe one: the model is handed only tools that fetch.
 * <b>write</b> additionally offers the tools that create, edit and publish content — and each
 * of those still stops for the operator's explicit approval, with the exact payload shown,
 * before it runs. The switch decides what the model is <i>told about</i>; the approval decides
 * what actually happens. Both, because either alone is wrong: a model that is never offered a
 * write tool cannot draft anything, and one that is offered them with no gate can publish on a
 * misread instruction.</p>
 */
export type AiAccessMode = 'read' | 'write';

export interface AssistantTool {
  name: string;
  description: string;
  input_schema: Record<string, unknown>;
  /**
   * The permission a caller must hold for this tool to exist at all.
   *
   * <p><b>Tools the caller cannot use are never offered to the model.</b> Not disabled, not
   * refused at call time — absent from the tool list it is given. A model that is told a tool
   * exists will reach for it, and an assistant that keeps announcing it cannot do the thing it
   * just offered reads as broken rather than as careful. The server refuses independently, as
   * it does for the UI; this stops the conversation going somewhere it cannot end.</p>
   */
  permission?: string;
  /** True for anything that writes. Offered only in write mode, and only after approval. */
  mutates?: boolean;
  /**
   * Query-key roots to invalidate once this tool has run.
   *
   * <p>Content writes raise no `ResourceChanged` on the tenant hub — that carries notification
   * kinds, and authoring does not raise one — so a list left open behind the dock would keep
   * showing the world as it was before the assistant changed it.</p>
   */
  invalidates?: string[];
  /** A one-line, human-readable account of what this call will do, for the approval card. */
  summarize?: (input: Record<string, unknown>) => string;
  run: (input: Record<string, unknown>) => Promise<string>;
}

const str = (input: Record<string, unknown>, key: string): string | undefined => {
  const value = input[key];
  return typeof value === 'string' && value.trim() ? value.trim() : undefined;
};

const obj = (input: Record<string, unknown>, key: string): Record<string, unknown> => {
  const value = input[key];
  return value && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
};

/** Required-argument accessor: a missing id must fail loudly rather than hit `/content/undefined`. */
const required = (input: Record<string, unknown>, key: string): string => {
  const value = str(input, key);
  if (!value) throw new Error(`The "${key}" argument is required.`);
  return value;
};

/**
 * What the assistant can do in the admin console.
 *
 * <p>Every one of these is a real call to the same API the UI uses, carrying the caller's own
 * token. That is the whole design: the assistant has exactly the reach its operator has, no
 * more, and there is no second code path that could drift from the first.</p>
 *
 * <p>The writing tools are gated twice over — the access switch, then a per-call approval that
 * shows the operator the payload — because the server cannot tell an edit the operator asked
 * for from one the model talked itself into.</p>
 */
export const ASSISTANT_TOOLS: AssistantTool[] = [
  {
    name: 'list_content',
    description:
      'List content items in this workspace. Returns id, title, content type and whether each is published.',
    permission: Perm.ContentRead,
    input_schema: {
      type: 'object',
      properties: {
        instanceId: { type: 'string', description: 'Plugin instance id to list from.' },
        contentType: { type: 'string', description: 'Content type name, e.g. "article".' },
      },
      additionalProperties: false,
    },
    run: async (input) => {
      const query = new URLSearchParams();
      const instance = str(input, 'instanceId');
      const type = str(input, 'contentType');
      if (instance) query.set('instanceId', instance);
      if (type) query.set('contentType', type);
      return JSON.stringify(await api.get(`/admin/content?${query}`));
    },
  },
  {
    name: 'get_content',
    description:
      'Read one content item in full by its id: its slug, status and the current draft field values.',
    permission: Perm.ContentRead,
    input_schema: {
      type: 'object',
      properties: { id: { type: 'string', description: 'The content item id.' } },
      required: ['id'],
      additionalProperties: false,
    },
    run: async (input) =>
      JSON.stringify(await api.get(`/admin/content/${encodeURIComponent(required(input, 'id'))}`)),
  },
  {
    /*
     * The tool that makes authoring possible at all. Without it the model knows an "article"
     * exists but not that it is made of `title`, `perex` and `body`, so it invents plausible
     * field names and writes a draft the editor renders as empty.
     */
    name: 'describe_content_types',
    description:
      'List the collections in this workspace and, for each, the content types it can hold and the exact fields each type is made of (name, type, whether required). Call this before creating or updating content so the field names are right.',
    permission: Perm.ContentRead,
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => {
      type Manifest = {
        id: string;
        name: string;
        contentTypes: {
          name: string;
          fields: { name: string; type: string; required: boolean; description?: string }[];
        }[];
      };
      type Instance = { id: string; pluginId: string; slug: string; name: string; enabled: boolean };
      const [catalog, instances] = await Promise.all([
        api.get<Manifest[]>('/admin/plugins/catalog'),
        api.get<Instance[]>('/admin/plugins/instances'),
      ]);
      const collections = instances
        .filter((i) => i.enabled)
        .map((instance) => {
          const manifest = catalog.find((m) => m.id === instance.pluginId);
          return {
            instanceId: instance.id,
            slug: instance.slug,
            name: instance.name,
            plugin: manifest?.name ?? instance.pluginId,
            contentTypes: (manifest?.contentTypes ?? []).map((ct) => ({
              contentType: ct.name,
              fields: ct.fields,
            })),
          };
        });
      return JSON.stringify(collections);
    },
  },
  {
    name: 'list_plugins',
    description:
      'List the plugin instances installed in this workspace and whether each is enabled.',
    permission: Perm.PluginsManage,
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => JSON.stringify(await api.get('/admin/plugins/instances')),
  },
  {
    name: 'get_analytics',
    description:
      'Traffic for this workspace over a number of days: visitors, sessions, page views, events, and the top paths and sources.',
    permission: Perm.AnalyticsRead,
    input_schema: {
      type: 'object',
      properties: {
        days: { type: 'number', description: 'Window length in days. 7, 30, 90 or 365.' },
      },
      additionalProperties: false,
    },
    run: async (input) => {
      const days = typeof input.days === 'number' ? input.days : 30;
      return JSON.stringify(await api.get(`/admin/analytics?days=${days}`));
    },
  },
  {
    name: 'list_sites',
    description: 'List the sites in this workspace, with their render mode and deployment state.',
    permission: Perm.SiteEdit,
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => JSON.stringify(await api.get('/admin/sites')),
  },
  {
    name: 'search_media',
    description: 'Search the media library by file name. Returns id, file name, category and size.',
    permission: Perm.MediaRead,
    input_schema: {
      type: 'object',
      properties: { query: { type: 'string', description: 'Part of a file name.' } },
      additionalProperties: false,
    },
    run: async (input) => {
      const query = str(input, 'query');
      const assets =
        await api.get<{ id: string; fileName: string; category: string; sizeBytes: number }[]>(
          '/admin/media',
        );
      const needle = query?.toLowerCase();
      const matching = needle
        ? assets.filter((a) => a.fileName.toLowerCase().includes(needle))
        : assets;
      // Capped rather than paged: this is context for a sentence, not a listing, and a
      // thousand file names would crowd out the actual question.
      return JSON.stringify(matching.slice(0, 50));
    },
  },

  // ---- Writing. Offered only in write mode, and only past the approval card. ----

  {
    name: 'create_content',
    description:
      'Create a new content item as a DRAFT. It is not published and nothing is publicly visible until publish_content is called. Field names in `data` must match the content type — call describe_content_types first.',
    permission: Perm.ContentWrite,
    mutates: true,
    invalidates: ['content'],
    input_schema: {
      type: 'object',
      properties: {
        instanceId: { type: 'string', description: 'The collection (plugin instance) id.' },
        contentType: { type: 'string', description: 'Content type name, e.g. "article".' },
        slug: {
          type: 'string',
          description: 'URL identifier, lower case with hyphens. Must be unique within the type.',
        },
        data: {
          type: 'object',
          description: 'The field values, keyed by field name.',
          additionalProperties: true,
        },
      },
      required: ['instanceId', 'contentType', 'slug', 'data'],
      additionalProperties: false,
    },
    summarize: (input) =>
      `Create a draft "${str(input, 'slug') ?? '?'}" of type ${str(input, 'contentType') ?? '?'}`,
    run: async (input) =>
      JSON.stringify(
        await api.post('/admin/content', {
          pluginInstanceId: required(input, 'instanceId'),
          contentType: required(input, 'contentType'),
          slug: required(input, 'slug'),
          data: obj(input, 'data'),
        }),
      ),
  },
  {
    name: 'update_content',
    description:
      'Change field values on an existing item, as a new draft version. Only the fields given in `data` are changed; the rest are left as they are. Nothing published changes until publish_content is called.',
    permission: Perm.ContentWrite,
    mutates: true,
    invalidates: ['content'],
    input_schema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'The content item id.' },
        data: {
          type: 'object',
          description: 'The field values to change, keyed by field name.',
          additionalProperties: true,
        },
      },
      required: ['id', 'data'],
      additionalProperties: false,
    },
    summarize: (input) =>
      `Update content ${str(input, 'id') ?? '?'} (${Object.keys(obj(input, 'data')).join(', ') || 'no fields'})`,
    run: async (input) => {
      const id = required(input, 'id');
      /*
       * Read-merge-write, because PUT replaces the draft wholesale. A model asked to "fix the
       * headline" sends one field; posting that alone would silently empty every other field
       * of the item, and the loss would only surface the next time somebody opened it.
       */
      const current = await api.get<{ draft: Record<string, unknown> | null }>(
        `/admin/content/${encodeURIComponent(id)}`,
      );
      const merged = { ...(current.draft ?? {}), ...obj(input, 'data') };
      return JSON.stringify(
        await api.put(`/admin/content/${encodeURIComponent(id)}`, { data: merged }),
      );
    },
  },
  {
    name: 'publish_content',
    description:
      'Publish an existing content item, making its current draft publicly visible. Only when the operator has asked for it.',
    permission: Perm.ContentPublish,
    mutates: true,
    invalidates: ['content'],
    input_schema: {
      type: 'object',
      properties: { id: { type: 'string', description: 'The content item id.' } },
      required: ['id'],
      additionalProperties: false,
    },
    summarize: (input) => `Publish content ${str(input, 'id') ?? '?'} — this makes it public`,
    run: async (input) =>
      JSON.stringify(
        await api.post(`/admin/content/${encodeURIComponent(required(input, 'id'))}/publish`, {}),
      ),
  },
];

/**
 * The tools this caller may actually use, in this access mode.
 *
 * <p>See the note on `permission` for why an unusable tool is absent rather than refused, and
 * the note on {@link AiAccessMode} for why read is the default.</p>
 */
export function toolsFor(me: MyPermissions | undefined, mode: AiAccessMode = 'read'): AssistantTool[] {
  return ASSISTANT_TOOLS.filter(
    (tool) =>
      (mode === 'write' || !tool.mutates) && (!tool.permission || can(me, tool.permission)),
  );
}

/**
 * Whether write access is even offerable to this caller.
 *
 * <p>False hides the switch rather than disabling it: a control that can never be turned on for
 * this role is noise, and the tooltip it would need says nothing the empty tool list does not.</p>
 */
export function canUseWriteAccess(me: MyPermissions | undefined): boolean {
  return ASSISTANT_TOOLS.some((tool) => tool.mutates && (!tool.permission || can(me, tool.permission)));
}

/** The wire form the model is given — the `run` function is ours, not its business. */
export function toolDefinitions(tools: readonly AssistantTool[]) {
  return tools.map(({ name, description, input_schema }) => ({ name, description, input_schema }));
}
