import type { QueryClient } from '@tanstack/react-query';
import { type AiPromptOverrides, DEFAULT_AGENT_MAX_TURNS } from '@dcms/core';
import { api } from '../../lib/api';
import { queryClient as shared } from '../../lib/queryClient';

/** What the platform console decided for this tenant's agents: their turn limit and prompts. */
export interface AgentPolicy {
  maxTurns: number;
  prompts: AiPromptOverrides;
}

export const agentPolicyKey = ['ai', 'agent-policy'] as const;

/**
 * The tenant's agent policy, cached for a minute. A policy that cannot be read is the shipped
 * one: an agent with its default limit and prompts beats an agent that will not start.
 */
export async function agentPolicy(client: QueryClient = shared): Promise<AgentPolicy> {
  try {
    const found = await client.fetchQuery({
      queryKey: agentPolicyKey,
      staleTime: 60_000,
      queryFn: () => api.get<Partial<AgentPolicy>>('/admin/ai/agent-policy'),
    });
    return {
      maxTurns: typeof found?.maxTurns === 'number' && found.maxTurns > 0 ? found.maxTurns : DEFAULT_AGENT_MAX_TURNS,
      prompts: found?.prompts && typeof found.prompts === 'object' ? found.prompts : {},
    };
  } catch {
    return { maxTurns: DEFAULT_AGENT_MAX_TURNS, prompts: {} };
  }
}
