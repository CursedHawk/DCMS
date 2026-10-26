import { api } from '../../../lib/api';
import type { ToolSpec } from '../../agent/contracts';
import { ASSISTANT_TOOLS, type AssistantTool, type ToolContext } from '../../assistant/tools';
import type { CheckToolContext } from './checkTools';
import { SKILLS, readSkill } from './skills';

/**
 * The tools that reach outside the site's own files: tenant content, the site's API, and the
 * on-demand knowledge library.
 *
 * <p>These are what make the IDE agent more than a code editor — a site that fetches content
 * from the tenant API is much easier to build when the thing writing it can see what content
 * types actually exist.</p>
 */

/** The IDE's tool context plus what a tenant tool needs. */
export interface TenantToolContext extends CheckToolContext {
  assistant: ToolContext;
}

/**
 * Adapt a console assistant tool for the shared runtime.
 *
 * <p>The one difference between the two registries is the return type: assistant tools hand back
 * a bare string, the contract asks for a `ToolOutcome`. This is the adapter rather than a
 * rewrite of all sixteen, and that is the right call rather than a shortcut — the extra fields
 * in `ToolOutcome` describe <i>workspace file</i> effects (which paths changed, was the result
 * clipped), and a tool that publishes an article touches no workspace file. There is nothing
 * real for those fields to carry.</p>
 *
 * <p>A throwing tool becomes an error outcome here rather than propagating: the runtime already
 * catches, but converting at the boundary keeps the assistant's own error messages intact
 * instead of flattening them into a generic failure.</p>
 */
export function fromAssistantTool(tool: AssistantTool): ToolSpec<TenantToolContext> {
  return {
    name: tool.name,
    description: tool.description,
    input_schema: tool.input_schema,
    permission: tool.permission,
    risk: tool.risk,
    invalidates: tool.invalidates,
    summarize: tool.summarize,
    describe: tool.describe,
    run: async (input, ctx) => {
      try {
        return { content: await tool.run(input, ctx.assistant) };
      } catch (error) {
        return { content: (error as Error)?.message ?? 'The tool failed.', isError: true };
      }
    },
  };
}

/** Every tenant tool, adapted. Which of them a run is actually offered is decided by scope. */
export const TENANT_TOOLS: ToolSpec<TenantToolContext>[] = ASSISTANT_TOOLS.map(fromAssistantTool);

/**
 * Calling the site's own API the way the running site does.
 *
 * <p>The preview proxies the site's API calls to its environment's live data (ADR 0026: there is
 * no sandbox — safe experiments belong in a development environment). This exposes that same path
 * so the agent can verify an integration works instead of writing it and hoping; a POST is a real
 * write and is approved as one.</p>
 */
export const PREVIEW_TOOLS: ToolSpec<TenantToolContext>[] = [
  {
    name: 'preview_request',
    description:
      "Call the site's content API exactly as the running site would, in this environment. GET to check that an endpoint exists and what it returns before writing code against it; POST writes real data in this environment (a form submission, a record), so use it only when asked to verify a write end to end.",
    input_schema: {
      type: 'object',
      properties: {
        method: { type: 'string', enum: ['GET', 'POST'] },
        path: {
          type: 'string',
          description: 'Path under the delivery API, e.g. "/content/articles".',
        },
        body: { type: 'object', description: 'JSON body for POST.' },
      },
      required: ['path'],
      additionalProperties: false,
    },
    riskFor: (input) => (input.method === 'POST' ? 'dangerous' : 'safe'),
    describe: (input) => `${String(input.method ?? 'GET')} ${String(input.path ?? '')}`,
    summarize: (input) => `Call ${String(input.method ?? 'GET')} ${String(input.path ?? '')}`,
    maxResultChars: 6000,
    run: async (input, ctx) => {
      const method = input.method === 'POST' ? 'POST' : 'GET';
      const path = String(input.path ?? '');
      if (!path.startsWith('/')) {
        return { content: 'path must start with "/".', isError: true };
      }
      // Routed through the same same-origin preview proxy the iframe uses, so tenant resolution
      // is applied server-side exactly as it is for the site.
      const url = `/admin/sites/${ctx.siteId}/preview${path}`;
      try {
        const result =
          method === 'POST'
            ? await api.post<unknown>(url, input.body ?? {})
            : await api.get<unknown>(url);
        return { content: JSON.stringify(result, null, 2) };
      } catch (error) {
        return {
          content: `${method} ${path} failed: ${(error as Error)?.message ?? 'unknown error'}`,
          isError: true,
        };
      }
    },
  },
];

/**
 * On-demand knowledge, instead of a system prompt nobody can afford.
 *
 * <p>The alternative is pasting every convention the platform has into every request. This lists
 * what is available in a few dozen tokens and loads one only when the task needs it.</p>
 */
export const SKILL_TOOLS: ToolSpec<TenantToolContext>[] = [
  {
    name: 'read_skill',
    description: `Load DCMS reference material on demand. Available: ${Object.keys(SKILLS).join(', ')}. Read one when the task touches that area — do not guess at platform conventions.`,
    input_schema: {
      type: 'object',
      properties: { name: { type: 'string', enum: Object.keys(SKILLS) } },
      required: ['name'],
      additionalProperties: false,
    },
    describe: (input) => `read skill: ${String(input.name ?? '')}`,
    run: async (input) => {
      const content = readSkill(String(input.name ?? ''));
      if (!content) {
        return {
          content: `No such skill. Available: ${Object.keys(SKILLS).join(', ')}.`,
          isError: true,
        };
      }
      return { content };
    },
  },
];
