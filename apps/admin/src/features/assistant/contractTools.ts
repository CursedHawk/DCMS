import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api';
import type { ToolRisk } from '../agent/modes';
import type { AssistantTool, ToolContext } from './tools';

/**
 * Plugin contract operations as assistant tools (docs/adr/0016).
 *
 * <p>The server's AI catalog (`GET /admin/contracts?plane=ai`) already did the gating that
 * matters: only operations exposed to AI, only on instances the tenant opted in, and only the
 * ones this member's permissions reach. So a tool here carries no `permission` of its own — it
 * would duplicate a decision the server made and re-checks on every call.</p>
 *
 * <p>What this file adds is the mapping onto the assistant's own model: a stable tool name, the
 * contract's JSON Schema as the input schema, the operation's risk for the mode table, and the
 * untrusted-text wrapper for operations that return what site visitors wrote.</p>
 */

/** OpRisk as the server serialises it: a number (Read, Safe, Dangerous) or its name. */
type WireRisk = 0 | 1 | 2 | 'Read' | 'Safe' | 'Dangerous';

export interface CatalogOperation {
  name: string;
  description?: string | null;
  risk: WireRisk;
  permission?: string | null;
  returnsExternalText: boolean;
  inputSchema: Record<string, unknown>;
  outputSchema?: Record<string, unknown> | null;
}

export interface CatalogContract {
  id: string;
  description?: string | null;
  providers?: string[];
  instances: { id: string; slug: string; name: string; description: string }[];
  operations: CatalogOperation[];
}

/** The assistant's risk for a contract operation. A read carries none, as the built-in tools do. */
export function toolRisk(risk: WireRisk): ToolRisk | undefined {
  switch (risk) {
    case 1:
    case 'Safe':
      return 'safe';
    case 2:
    case 'Dangerous':
      return 'dangerous';
    default:
      return undefined;
  }
}

/** Provider tool-name rules: letters, digits, `_` and `-`, at most 64 characters. */
export function toolName(...parts: string[]): string {
  const snake = parts
    .join('_')
    .replace(/([a-z0-9])([A-Z])/g, '$1_$2')
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '_')
    .replace(/_+/g, '_')
    .replace(/^_|_$/g, '');
  return snake.slice(0, 64);
}

/**
 * One tool per operation per providing instance (a Roster for the crew and one for the
 * speakers are two tools, each described by its instance), and one per platform operation.
 */
export function contractTools(catalog: readonly CatalogContract[]): AssistantTool[] {
  const tools: AssistantTool[] = [];
  const seen = new Set<string>();
  for (const contract of catalog) {
    const targets = contract.instances.length > 0 ? contract.instances : [null];
    for (const instance of targets) {
      for (const op of contract.operations) {
        const name = instance
          ? toolName(instance.slug, contract.id.split('@')[0]!.split('.').pop()!, op.name)
          : toolName('platform', contract.id.split('@')[0]!, op.name);
        if (seen.has(name)) continue;
        seen.add(name);

        const where = instance
          ? `${instance.name} (${contract.id})${instance.description ? ` — ${instance.description}` : ''}`
          : contract.id;
        const query = new URLSearchParams({ plane: 'ai' });
        if (instance) query.set('instance', instance.slug);
        const url = `/admin/contracts/${encodeURIComponent(contract.id)}/${encodeURIComponent(op.name)}?${query}`;

        tools.push({
          name,
          description: `${op.description ?? op.name}. From ${where}.`,
          input_schema: op.inputSchema,
          risk: toolRisk(op.risk),
          untrustedSource: op.returnsExternalText ? `${where}: text written by site visitors` : undefined,
          summarize: (input) => `${words(op.name)} on ${instance?.name ?? contract.id}${brief(input)}`,
          describe: (input) => `Ran ${words(op.name).toLowerCase()} on ${instance?.name ?? contract.id}${brief(input)}`,
          // A plugin screen roots its query keys at `plugin:{slug}` to be refreshed by the
          // assistant's changes to that instance (see @dcms/plugin-ui).
          invalidates: instance ? [`plugin:${instance.slug}`] : undefined,
          maxResultChars: 16_000,
          run: async (input, context) =>
            JSON.stringify((await api.post(url, input, { headers: traceHeaders(context.trace) })) ?? null),
        });
      }
    }
  }
  return tools;
}

/** `ApplyChangeSet` → "Apply change set". */
function words(name: string): string {
  const spaced = name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/**
 * What a call will do, for the approval card: a change set lists its operations; anything else
 * shows its input, briefly. The card is where an operator decides, so it must say more than
 * the operation's name.
 */
export function brief(input: Record<string, unknown>): string {
  const ops = input.operations;
  if (Array.isArray(ops)) {
    const lines = ops.slice(0, 12).map((raw) => {
      const op = (raw ?? {}) as { op?: string; type?: string; target?: string; value?: { apiName?: string } };
      const subject = [op.target, op.value?.apiName].filter(Boolean).join(' → ');
      return `${op.op ?? '?'} ${op.type ?? '?'}${subject ? ` ${subject}` : ''}`;
    });
    const more = ops.length > 12 ? `, and ${ops.length - 12} more` : '';
    return `: ${lines.join('; ')}${more}`;
  }
  const json = JSON.stringify(input);
  if (!json || json === '{}') return '';
  return `: ${json.length > 160 ? `${json.slice(0, 160)}…` : json}`;
}

/** The trace a contract call carries, as headers the server reads only on the AI plane. */
function traceHeaders(trace: ToolContext['trace']): Record<string, string> {
  const headers: Record<string, string> = {};
  if (trace?.conversationId) headers['X-Dcms-Ai-Conversation'] = trace.conversationId;
  if (trace?.runId) headers['X-Dcms-Ai-Run'] = trace.runId;
  if (trace?.toolCallId) headers['X-Dcms-Ai-Tool-Call'] = trace.toolCallId;
  return headers;
}

/** The AI catalog for this member and tenant, as tools. Empty while loading or on failure. */
export function useContractTools(enabled = true): AssistantTool[] {
  const catalog = useQuery({
    queryKey: ['contracts', 'ai'],
    queryFn: () => api.get<CatalogContract[]>('/admin/contracts?plane=ai'),
    enabled,
    staleTime: 60_000,
  });
  // Stable between renders, so a session's callbacks that close over it are not rebuilt each time.
  // An unexpected payload (an older server, a proxy error page) means no plugin tools, never a crash.
  return useMemo(() => (Array.isArray(catalog.data) ? contractTools(catalog.data) : []), [catalog.data]);
}
