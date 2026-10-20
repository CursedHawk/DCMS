import type { Complexity } from './classify';

/**
 * How hard the model should think, chosen per turn rather than set once at maximum.
 *
 * <p>Every turn currently asks for adaptive thinking at high effort, including "change the button
 * text to Buy now". That is the default that makes an agent feel expensive and slow on exactly
 * the work that should feel instant.</p>
 *
 * <h3>Effort, not model names</h3>
 * <p>The brief describes routing between cheap and strong <i>models</i>. This routes effort
 * instead, and deliberately: the browser does not choose the model — ai-gateway resolves it from
 * the tenant's and the user's own settings, which may be Anthropic, OpenAI, or a local Ollama
 * with exactly one model installed. Sending a model name the workspace has not configured would
 * fail, and second-guessing a user who has deliberately pinned one is worse than not trying.</p>
 *
 * <p>What the request <i>can</i> carry safely is how much thinking to do and how many tokens to
 * allow, which is where most of the cost difference on a trivial task actually lives. When the
 * platform later grows a per-tenant model tier map, this is the one place that has to learn
 * about it.</p>
 */

export interface ModelParams {
  max_tokens: number;
  thinking: { type: 'adaptive' };
  output_config?: { effort: 'low' | 'medium' | 'high' };
}

/**
 * Parameters for one turn.
 *
 * <p>`repairing` raises effort regardless of the original classification: a run that has already
 * failed its build once has demonstrated that the cheap setting was not enough, and spending
 * more on the second attempt is cheaper than a third.</p>
 */
export function paramsFor(
  complexity: Complexity,
  options: { repairing?: boolean } = {},
): ModelParams {
  if (options.repairing) {
    return {
      max_tokens: 16000,
      thinking: { type: 'adaptive' },
      output_config: { effort: 'high' },
    };
  }

  switch (complexity) {
    case 'trivial':
      // Low effort and a small ceiling. Thinking stays adaptive rather than disabled: the
      // browser does not know the model, and Opus 5.5 / Sonnet 5.5 refuse a disabled-thinking
      // request outright. Low effort is what keeps a one-line edit cheap. A one-line edit that
      // needs deliberation was misclassified, and the repair path above catches that.
      return { max_tokens: 4000, thinking: { type: 'adaptive' }, output_config: { effort: 'low' } };

    case 'normal':
      return {
        max_tokens: 12000,
        thinking: { type: 'adaptive' },
        output_config: { effort: 'medium' },
      };

    case 'complex':
      return {
        max_tokens: 16000,
        thinking: { type: 'adaptive' },
        output_config: { effort: 'high' },
      };
  }
}

const EPHEMERAL = { type: 'ephemeral' } as const;

/**
 * Mark a request so a provider with prompt caching reuses everything it has already seen.
 *
 * <p>Caching is a prefix match over tools → system → messages, in that order. Two breakpoints:</p>
 * <ul>
 *   <li><b>Top-level <code>cache_control</code></b> — automatic caching, which moves the
 *   breakpoint to the last block of every request. Each turn of a run then reads the whole
 *   conversation so far from the cache and pays full price only for what was appended.</li>
 *   <li><b>One on the system prompt</b> — so that when `pruneHistory` does rewrite the front of
 *   the history, tools and system are still a hit and only the messages are rebuilt.</li>
 * </ul>
 *
 * <p>A breakpoint on the last <i>tool</i>, which this used to set, cached the tools alone: the
 * system prompt and every message after it were paid in full on every turn.</p>
 *
 * <p>Providers without caching ignore the field, and ai-gateway's OpenAI bridge rebuilds the
 * request without it (OpenAI caches prefixes on its own).</p>
 */
export function withCacheHints(body: Record<string, unknown>): Record<string, unknown> {
  const system =
    typeof body.system === 'string' && body.system
      ? [{ type: 'text', text: body.system, cache_control: EPHEMERAL }]
      : body.system;
  return { ...body, system, cache_control: EPHEMERAL };
}
