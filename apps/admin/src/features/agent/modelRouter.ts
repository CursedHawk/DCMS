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
  thinking: { type: 'adaptive' | 'disabled' };
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
      // No thinking budget and a small ceiling. A one-line edit that needs deliberation was
      // misclassified, and the repair path above catches that.
      return { max_tokens: 4000, thinking: { type: 'disabled' }, output_config: { effort: 'low' } };

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

/**
 * Order a request so its stable half can be cached by providers that support it.
 *
 * <p>Prompt caching keys on an exact prefix, so anything that changes between turns must come
 * after everything that does not. The system prompt and the tool definitions are fixed for a
 * whole run; the conversation is not. Interleaving them costs the cache on every turn.</p>
 *
 * <p>`cache_control` is set on the last tool definition, which marks the end of the stable
 * prefix. Providers that do not understand the field ignore it, so this is safe to send
 * everywhere — and ai-gateway's OpenAI bridge already drops fields it does not translate.</p>
 */
export function withCacheHints(body: Record<string, unknown>): Record<string, unknown> {
  const tools = body.tools;
  if (!Array.isArray(tools) || tools.length === 0) return body;

  const marked = tools.map((tool, index) =>
    index === tools.length - 1
      ? { ...(tool as object), cache_control: { type: 'ephemeral' } }
      : tool,
  );

  // Key order is what a provider hashes for a prefix cache, so the stable fields are written
  // first and `messages` last.
  return {
    system: body.system,
    tools: marked,
    ...omit(body, ['system', 'tools', 'messages']),
    messages: body.messages,
  };
}

function omit(source: Record<string, unknown>, keys: readonly string[]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(source)) {
    if (!keys.includes(key)) out[key] = value;
  }
  return out;
}
