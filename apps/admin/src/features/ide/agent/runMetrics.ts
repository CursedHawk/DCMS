import type { TurnUsage } from './client';

/**
 * What one agent run cost, recorded so the rework can be judged instead of asserted.
 *
 * <p>The goal the IDE agent rework is measured against — fewer tokens, fewer API calls, faster —
 * is a standard, and a standard nothing can check gets re-argued every time somebody doubts it.
 * `ai-gateway` already counts tokens per API call into a Prometheus counter tagged by tenant,
 * provider and model; what no existing instrument has is the notion of a <i>run</i>: one task,
 * however many turns it took. That is the unit the improvement happens in.</p>
 *
 * <p>These records are exported by hand from the agent panel and folded into
 * `benchmarks/ai-agent.json` by `scripts/ai-bench`. Committing them is the point — a number in a
 * chat log is gone by the time the comparison matters.</p>
 */
export interface AgentRunMetrics {
  /** Which fixed benchmark task this was, when the run was a benchmark rather than real work. */
  task: string | null;
  /** ISO 8601, so a baseline says when it was taken. */
  at: string;
  /** Model and provider, because a run is only comparable against the same pair. */
  model: string | null;
  provider: string | null;

  /** Model turns — the API-call count the brief asks to reduce. */
  turns: number;
  /** Tool calls executed across the whole run. */
  toolCalls: number;
  /** Per-tool counts, which is where a bad tool surface shows up as a read-everything loop. */
  toolCallsByName: Record<string, number>;

  /** Summed across turns. Null when the provider never reported the prompt side — see TurnUsage. */
  inputTokens: number | null;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;

  wallMs: number;
  /** How the run ended. A failed run's numbers are real but not comparable to a completed one. */
  outcome: 'completed' | 'stopped' | 'failed';
}

/** A run in progress. Turn usage is summed, not maxed — unlike within a single turn. */
export interface RunAccumulator {
  startedAt: number;
  turns: number;
  toolCalls: number;
  toolCallsByName: Record<string, number>;
  /** Sum of the turns that DID report a prompt side. Meaningless while `inputUnknown` is set. */
  inputSum: number;
  /** Latched by the first turn that reported no prompt side; never cleared. */
  inputUnknown: boolean;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

export function startRun(): RunAccumulator {
  return {
    startedAt: Date.now(),
    turns: 0,
    toolCalls: 0,
    toolCallsByName: {},
    inputSum: 0,
    inputUnknown: false,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
  };
}

/**
 * Fold one completed turn into the run.
 *
 * <p>Across turns the numbers are <b>summed</b>: each turn is a separate billed request, and the
 * whole point of the measurement is that a run costs the sum of its turns. That is the opposite
 * of the rule inside a turn, where a repeated running total is maxed.</p>
 *
 * <p>A turn whose prompt side was never reported leaves the run's `inputTokens` null for good.
 * One unknown turn makes the run's input total unknown, and a partial sum presented as a total
 * is worse than no number: it reads as a saving.</p>
 */
export function recordTurn(run: RunAccumulator, usage: TurnUsage | null): void {
  run.turns += 1;
  if (!usage || usage.inputTokens === null) {
    // One turn nobody can price makes the run's input total unknown, permanently.
    run.inputUnknown = true;
  } else {
    run.inputSum += usage.inputTokens;
  }
  if (!usage) return;
  run.outputTokens += usage.outputTokens;
  run.cacheReadTokens += usage.cacheReadTokens;
  run.cacheCreationTokens += usage.cacheCreationTokens;
}

export function recordToolCall(run: RunAccumulator, name: string): void {
  run.toolCalls += 1;
  run.toolCallsByName[name] = (run.toolCallsByName[name] ?? 0) + 1;
}

export function finishRun(
  run: RunAccumulator,
  outcome: AgentRunMetrics['outcome'],
  meta: { task?: string | null; model?: string | null; provider?: string | null } = {},
): AgentRunMetrics {
  return {
    task: meta.task ?? null,
    at: new Date().toISOString(),
    model: meta.model ?? null,
    provider: meta.provider ?? null,
    turns: run.turns,
    toolCalls: run.toolCalls,
    toolCallsByName: { ...run.toolCallsByName },
    inputTokens: run.inputUnknown ? null : run.inputSum,
    outputTokens: run.outputTokens,
    cacheReadTokens: run.cacheReadTokens,
    cacheCreationTokens: run.cacheCreationTokens,
    wallMs: Date.now() - run.startedAt,
    outcome,
  };
}
