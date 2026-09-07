import { can, type MyPermissions } from '@dcms/core';
import { Perm } from '../../lib/permissions';
import { api } from '../../lib/api';

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
  /** True for anything that writes. Gated behind explicit approval. */
  mutates?: boolean;
  run: (input: Record<string, unknown>) => Promise<string>;
}

const str = (input: Record<string, unknown>, key: string): string | undefined => {
  const value = input[key];
  return typeof value === 'string' && value.trim() ? value.trim() : undefined;
};

/**
 * What the assistant can do in the admin console.
 *
 * <p>Every one of these is a real call to the same API the UI uses, carrying the caller's own
 * token. That is the whole design: the assistant has exactly the reach its operator has, no
 * more, and there is no second code path that could drift from the first.</p>
 *
 * <p>Read-only for now, on purpose. A model that can publish content or delete media needs an
 * approval flow with a preview of precisely what will change, and shipping the write tools
 * before that exists would be trading a real risk for a demo.</p>
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
];

/** The tools this caller may actually use. See the note on `permission`. */
export function toolsFor(me: MyPermissions | undefined): AssistantTool[] {
  return ASSISTANT_TOOLS.filter((tool) => !tool.permission || can(me, tool.permission));
}

/** The wire form the model is given — the `run` function is ours, not its business. */
export function toolDefinitions(tools: readonly AssistantTool[]) {
  return tools.map(({ name, description, input_schema }) => ({ name, description, input_schema }));
}
