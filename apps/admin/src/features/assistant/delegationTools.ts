import { Perm } from '../../lib/permissions';
import { useAgentHost } from '../agent/hostStore';
import type { AssistantTool } from './tools';
import { required, str } from './toolArgs';

/**
 * How the console assistant changes a site: by asking the site's own agent (ADR 0033).
 *
 * <p>The assistant has no file, compiler or builder tools and is not given any: a site's agent
 * — IDE, Mode A builder or visual builder — is the one that knows that site's kind, checks its
 * own work and keeps its specialisation. A request runs in the background in this tab, in the
 * site's agent, and shows in the dock's Agents tab while it does.</p>
 *
 * <p>Deliberately NOT part of `ASSISTANT_TOOLS`: that list is also handed to site agents as their
 * tenant tools, and an agent able to delegate to agents is a loop nobody asked for.</p>
 */
export const DELEGATION_TOOLS: AssistantTool[] = [
  {
    name: 'request_site_change',
    description:
      "Ask a site's own agent to make a change to that site — pages, components, code, styles, content wiring. This is the ONLY way to change a site's files: you cannot edit them yourself. Get the site id from list_sites first. Write the task so it stands on its own: the site agent has not seen this conversation. The agent works in the background (one at a time per site; a busy site queues it). Returns a request id: check it with site_change_status before telling the user what changed.",
    permission: Perm.SiteEdit,
    // It starts an agent that edits a site's working draft unattended: worth one approval.
    risk: 'dangerous',
    input_schema: {
      type: 'object',
      properties: {
        siteId: { type: 'string', description: 'The site id, from list_sites.' },
        task: { type: 'string', description: 'What to change, completely and specifically.' },
      },
      required: ['siteId', 'task'],
      additionalProperties: false,
    },
    summarize: (input) => `Ask the site's agent: ${str(input, 'task') ?? '?'}`,
    describe: () => 'Handed the change to the site agent',
    run: async (input, ctx) => {
      // Loaded on first use: the run machinery is the editors' weight, not the shell's.
      const { requestSiteChange } = await import('../agent/host');
      const request = await requestSiteChange({
        siteId: required(input, 'siteId'),
        task: required(input, 'task'),
        requestedBy: ctx.trace?.conversationId ?? null,
      });
      const queued = useAgentHost.getState().sessions[request.siteKey]?.queue.some((q) => q.requestId === request.id);
      return JSON.stringify({
        requestId: request.id,
        site: request.siteName,
        state: queued ? 'queued behind the agent already working on this site' : 'started',
        note: 'It runs in the background; the user can watch it in the Agents tab of this panel.',
      });
    },
  },
  {
    name: 'site_change_status',
    description:
      'How a request_site_change is going: queued, running, completed, paused (ran out of turns — the user can continue it in the site\'s agent), failed or stopped; and once done, the files it changed, whether its checks passed and the agent\'s own summary.',
    permission: Perm.SiteEdit,
    input_schema: {
      type: 'object',
      properties: { requestId: { type: 'string' } },
      required: ['requestId'],
      additionalProperties: false,
    },
    untrustedSource: "a site agent's summary of its own work",
    run: async (input) => {
      const request = useAgentHost.getState().requests[required(input, 'requestId')];
      if (!request) return JSON.stringify({ error: 'No such request in this browser tab. Requests do not survive a reload.' });
      const session = useAgentHost.getState().sessions[request.siteKey];
      return JSON.stringify({
        site: request.siteName,
        status: request.status,
        ...(request.status === 'running' ? { turns: session?.turns, doing: session?.step } : {}),
        changedFiles: request.changes,
        checks: request.validation ? (request.validation.ok ? 'passed' : 'failed') : 'not run',
        summary: request.summary,
      });
    },
  },
];
