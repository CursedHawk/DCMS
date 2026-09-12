import { can, type MyPermissions } from '@dcms/core';
import { Perm } from '../../lib/permissions';
import { api } from '../../lib/api';
import type { Attachment } from './attachments';
import type { ToolSpec } from '../agent/contracts';
import { type AiMode, decide } from '../agent/modes';

export type { ToolRisk } from '../agent/modes';

/** Everything a tool is given beyond its own arguments. */
export interface ToolContext {
  /** Files the operator attached to this conversation, for `upload_media`. */
  attachments: readonly Attachment[];
  /** Records an upload so the same bytes are not sent twice. */
  onUploaded: (name: string, assetId: string) => void;
}

/**
 * A console assistant tool.
 *
 * <p>Every field except `run` comes from the shared {@link ToolSpec}, so the two agent surfaces
 * cannot drift on what a tool declares — name, schema, permission, risk, invalidation and the
 * two narration hooks are defined once, in `features/agent/contracts.ts`.</p>
 *
 * <p><b>The one deliberate divergence is `run`.</b> The shared contract has a tool return a
 * `ToolOutcome` — content plus the paths it touched plus whether it was truncated — because
 * result-size discipline and the change-review pane both need that. These sixteen tools still
 * return a bare string. Converging them is Phase 5 of the rework, and this type names the gap
 * rather than hiding it behind a structural type that happens to fit.</p>
 */
export type AssistantTool = Omit<ToolSpec<ToolContext>, 'run'> & {
  run: (input: Record<string, unknown>, context: ToolContext) => Promise<string>;
};

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

const ids = (input: Record<string, unknown>, key: string): string[] => {
  const value = input[key];
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [];
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
      type Instance = {
        id: string;
        pluginId: string;
        slug: string;
        name: string;
        enabled: boolean;
      };
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
    risk: 'safe',
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
    describe: (input) => `Created draft ${str(input, 'slug') ?? 'content'}`,
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
    risk: 'safe',
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
    describe: (input) => {
      const fields = Object.keys(obj(input, 'data'));
      return fields.length ? `Edited ${fields.join(', ')}` : 'Edited content';
    },
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
      const changes = obj(input, 'data');
      const draft = current.draft ?? {};
      const merged = { ...draft, ...changes };
      const item = await api.put(`/admin/content/${encodeURIComponent(id)}`, { data: merged });

      /*
       * The before/after of the fields that actually changed, returned rather than recomputed.
       *
       * The transcript's work card shows it, and it is free here — the merge above has already
       * read the old draft. Recomputing it later is impossible: by the time anyone opens the
       * card the draft is the new one. It also means a conversation resumed next week still
       * shows what the edit did, because it is in the stored tool result.
       */
      const before: Record<string, unknown> = {};
      for (const key of Object.keys(changes)) before[key] = draft[key] ?? null;
      return JSON.stringify({ item, changed: { before, after: changes } });
    },
  },
  {
    name: 'publish_content',
    description:
      'Publish an existing content item, making its current draft publicly visible. Only when the operator has asked for it.',
    permission: Perm.ContentPublish,
    risk: 'dangerous',
    invalidates: ['content'],
    input_schema: {
      type: 'object',
      properties: { id: { type: 'string', description: 'The content item id.' } },
      required: ['id'],
      additionalProperties: false,
    },
    summarize: (input) => `Publish content ${str(input, 'id') ?? '?'} — this makes it public`,
    describe: () => 'Published',
    run: async (input) =>
      JSON.stringify(
        await api.post(`/admin/content/${encodeURIComponent(required(input, 'id'))}/publish`, {}),
      ),
  },
  {
    name: 'unpublish_content',
    description:
      'Take a published item back off the public site. Its draft is kept, so the work is not lost — only its visibility changes.',
    permission: Perm.ContentPublish,
    risk: 'dangerous',
    invalidates: ['content'],
    input_schema: {
      type: 'object',
      properties: { id: { type: 'string', description: 'The content item id.' } },
      required: ['id'],
      additionalProperties: false,
    },
    summarize: (input) => `Unpublish content ${str(input, 'id') ?? '?'} — it stops being public`,
    describe: () => 'Unpublished',
    run: async (input) =>
      JSON.stringify(
        await api.post(`/admin/content/${encodeURIComponent(required(input, 'id'))}/unpublish`, {}),
      ),
  },
  {
    /*
     * Dangerous despite writing nothing today: it is a publish with a timer on it, and the
     * operator who would have been asked before the publish is not going to be there when it
     * fires.
     */
    name: 'schedule_content',
    description:
      'Schedule an item to publish at a future time. Replaces any schedule it already has. Use ISO 8601 with an offset, e.g. 2026-10-01T09:00:00Z.',
    permission: Perm.ContentPublish,
    risk: 'dangerous',
    invalidates: ['content'],
    input_schema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'The content item id.' },
        publishAt: { type: 'string', description: 'When to publish, ISO 8601 with offset.' },
      },
      required: ['id', 'publishAt'],
      additionalProperties: false,
    },
    summarize: (input) =>
      `Publish content ${str(input, 'id') ?? '?'} automatically at ${str(input, 'publishAt') ?? '?'}`,
    describe: (input) => `Scheduled for ${str(input, 'publishAt') ?? 'later'}`,
    run: async (input) =>
      JSON.stringify(
        await api.post(`/admin/content/${encodeURIComponent(required(input, 'id'))}/schedule`, {
          publishAt: required(input, 'publishAt'),
        }),
      ),
  },
  {
    /*
     * The model has no bytes and cannot invent any. This uploads a file the operator attached
     * to the conversation — the paperclip in the composer — which is why it takes a file name
     * rather than a URL: a URL would be this app fetching an arbitrary address on the
     * operator's authority, which is a different feature with a different threat model.
     */
    name: 'upload_media',
    description:
      'Upload a file the operator attached to this conversation into the media library. Use the exact file name from the attachment list. Returns the new asset id, which can be used in content fields.',
    permission: Perm.MediaWrite,
    risk: 'safe',
    invalidates: ['media'],
    input_schema: {
      type: 'object',
      properties: {
        fileName: { type: 'string', description: 'The attached file name, exactly as listed.' },
        folderId: { type: 'string', description: 'Media folder id to file it under, if any.' },
      },
      required: ['fileName'],
      additionalProperties: false,
    },
    summarize: (input) => `Upload ${str(input, 'fileName') ?? '?'} to the media library`,
    describe: (input) => `Uploaded ${str(input, 'fileName') ?? 'a file'}`,
    run: async (input, { attachments, onUploaded }) => {
      const name = required(input, 'fileName');
      const attachment = attachments.find((a) => a.name === name);
      if (!attachment) {
        // Named, not counted: "no such attachment" with the list is a message the model can
        // act on, where "not found" makes it guess again with the same wrong name.
        const known = attachments.map((a) => a.name).join(', ') || 'nothing';
        throw new Error(`No file named "${name}" is attached. Attached: ${known}.`);
      }
      if (attachment.assetId) {
        return JSON.stringify({ id: attachment.assetId, alreadyUploaded: true });
      }

      const form = new FormData();
      form.append('file', attachment.file, attachment.name);
      const folder = str(input, 'folderId');
      if (folder) form.append('folderId', folder);

      const result = await api.upload<{ id: string; category: string; status: string }>(
        '/admin/media',
        form,
      );
      onUploaded(attachment.name, result.id);
      return JSON.stringify(result);
    },
  },
  {
    name: 'create_media_folder',
    description: 'Create a folder in the media library, optionally inside another folder.',
    permission: Perm.MediaWrite,
    risk: 'safe',
    invalidates: ['media'],
    input_schema: {
      type: 'object',
      properties: {
        name: { type: 'string', description: 'Folder name.' },
        parentId: { type: 'string', description: 'Parent folder id, for a nested folder.' },
      },
      required: ['name'],
      additionalProperties: false,
    },
    summarize: (input) => `Create the media folder "${str(input, 'name') ?? '?'}"`,
    describe: (input) => `Created folder ${str(input, 'name') ?? ''}`.trim(),
    run: async (input) =>
      JSON.stringify(
        await api.post('/admin/media/folders', {
          name: required(input, 'name'),
          parentId: str(input, 'parentId') ?? null,
        }),
      ),
  },
  {
    name: 'move_media',
    description:
      'Move media assets into a folder, or to the top level by omitting folderId. Filing, not deleting — the assets are unchanged.',
    permission: Perm.MediaWrite,
    risk: 'safe',
    invalidates: ['media'],
    input_schema: {
      type: 'object',
      properties: {
        ids: { type: 'array', items: { type: 'string' }, description: 'Asset ids to move.' },
        folderId: { type: 'string', description: 'Destination folder id. Omit for the top level.' },
      },
      required: ['ids'],
      additionalProperties: false,
    },
    summarize: (input) => `Move ${ids(input, 'ids').length} file(s)`,
    describe: (input) => `Moved ${ids(input, 'ids').length} file(s)`,
    run: async (input) =>
      JSON.stringify(
        await api.post('/admin/media/move', {
          ids: ids(input, 'ids'),
          folderId: str(input, 'folderId') ?? null,
        }),
      ),
  },
  {
    name: 'delete_media',
    description:
      'Permanently delete media assets. The files and every rendition of them are gone; anything using them will break.',
    permission: Perm.MediaWrite,
    risk: 'dangerous',
    invalidates: ['media'],
    input_schema: {
      type: 'object',
      properties: {
        ids: { type: 'array', items: { type: 'string' }, description: 'Asset ids to delete.' },
      },
      required: ['ids'],
      additionalProperties: false,
    },
    summarize: (input) =>
      `Permanently delete ${ids(input, 'ids').length} file(s) from the media library`,
    describe: (input) => `Deleted ${ids(input, 'ids').length} file(s)`,
    run: async (input) =>
      JSON.stringify(await api.post('/admin/media/delete', { ids: ids(input, 'ids') })),
  },
];

/**
 * The tools this caller may actually use, in this mode.
 *
 * <p>Two filters, and they answer different questions. The permission filter is about the
 * person: a tool their role cannot reach is absent rather than refused — see the note on
 * `permission`. The mode filter is about the posture they have chosen: in Read only the writing
 * tools are not offered either, so the model cannot propose a change it has been told it may
 * not make. Everything that survives both is offered; whether a call then stops for approval is
 * `decide`'s business, not this list's.</p>
 */
export function toolsFor(me: MyPermissions | undefined, mode: AiMode = 'read'): AssistantTool[] {
  return ASSISTANT_TOOLS.filter(
    (tool) =>
      decide(mode, tool.risk ?? 'read') !== 'unavailable' &&
      (!tool.permission || can(me, tool.permission)),
  );
}

/**
 * Whether any mode above Read only would give this caller anything.
 *
 * <p>False collapses the switch to a single state rather than showing three modes that all
 * behave identically: a control whose other positions can never do anything is noise, and the
 * tooltip it would need says nothing the empty tool list does not.</p>
 */
export function canWrite(me: MyPermissions | undefined): boolean {
  return ASSISTANT_TOOLS.some(
    (tool) => tool.risk && (!tool.permission || can(me, tool.permission)),
  );
}

/** Look one up by the name the model used. */
export function findTool(tools: readonly AssistantTool[], name: string): AssistantTool | undefined {
  return tools.find((tool) => tool.name === name);
}

/** The wire form the model is given — the `run` function is ours, not its business. */
export function toolDefinitions(tools: readonly AssistantTool[]) {
  return tools.map(({ name, description, input_schema }) => ({ name, description, input_schema }));
}
