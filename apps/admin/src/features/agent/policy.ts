import type { QueryClient } from '@tanstack/react-query';
import {
  type AiPromptOverrides,
  DEFAULT_AGENT_MAX_TURNS,
  DEFAULT_ASSISTANT_MAX_TURNS,
  DEFAULT_COMPACT_AT_TOKENS,
} from '@dcms/core';
import { api } from '../../lib/api';
import { queryClient as shared } from '../../lib/queryClient';

/** What the platform console decided for this tenant's agents: their turn limit and prompts. */
export interface AgentPolicy {
  /** Model turns a site agent may take in one run. */
  maxTurns: number;
  /** Model turns the console assistant may take for one question. */
  assistantMaxTurns: number;
  /** Prompt size, in tokens, at which a conversation is compacted. */
  compactAtTokens: number;
  prompts: AiPromptOverrides;
}

const DEFAULTS: AgentPolicy = {
  maxTurns: DEFAULT_AGENT_MAX_TURNS,
  assistantMaxTurns: DEFAULT_ASSISTANT_MAX_TURNS,
  compactAtTokens: DEFAULT_COMPACT_AT_TOKENS,
  prompts: {},
};

const positive = (value: unknown, fallback: number) =>
  typeof value === 'number' && value > 0 ? value : fallback;

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
      maxTurns: positive(found?.maxTurns, DEFAULTS.maxTurns),
      assistantMaxTurns: positive(found?.assistantMaxTurns, DEFAULTS.assistantMaxTurns),
      compactAtTokens: positive(found?.compactAtTokens, DEFAULTS.compactAtTokens),
      prompts: found?.prompts && typeof found.prompts === 'object' ? found.prompts : {},
    };
  } catch {
    return DEFAULTS;
  }
}
