import { renewSilently } from '../../../auth';
import { adminHeaders } from '../../../tenants';
import { runtimeConfig } from '../../../runtime-config';

/*
 * Streaming client for one assistant turn.
 *
 * The agent LOOP and its tools run in the browser (see useAgentSession); each model turn is
 * POSTed to the admin-api proxy, which injects the user's Vault-stored key server-side and
 * streams the answer back. The key never reaches the browser.
 *
 * The wire format here is Anthropic's, and stays that way whichever provider actually serves
 * the turn: ai-gateway translates to and from OpenAI Chat Completions for everything else, so
 * this loop has one protocol to parse rather than two. See `AnthropicOpenAiBridge`.
 */

const base = runtimeConfig.adminApiBase;
const MESSAGES_PATH = '/admin/ai/messages';

export interface TextBlock {
  type: 'text';
  text: string;
}
export interface ThinkingBlock {
  type: 'thinking';
  thinking: string;
  signature?: string;
}
export interface ToolUseBlock {
  type: 'tool_use';
  id: string;
  name: string;
  input: Record<string, unknown>;
}
export type ContentBlock =
  TextBlock | ThinkingBlock | ToolUseBlock | { type: string; [k: string]: unknown };

export interface ToolResultBlock {
  type: 'tool_result';
  tool_use_id: string;
  content: string;
  is_error?: boolean;
}

export interface Message {
  role: 'user' | 'assistant';
  content: string | ContentBlock[] | ToolResultBlock[];
}

/**
 * What one turn cost, as the provider reported it.
 *
 * <p>The gateway already scans these numbers off the stream on their way past
 * (`AnthropicUsageScanner`) and feeds them to a Prometheus counter tagged by tenant, provider and
 * model. That answers "what is the workspace spending"; it cannot answer "did this task get
 * cheaper", because a counter aggregated across calls has no notion of a run. Reading the same
 * numbers here — where the loop already parses the stream — gives per-run totals without putting
 * an unbounded run id onto the platform's hottest metric.</p>
 *
 * <p>Cache tokens are reported separately by the provider and are NOT included in
 * {@link inputTokens}; a cached prefix is the main thing Phase 6 is trying to buy, so folding the
 * two together would hide exactly the improvement it is meant to show.</p>
 *
 * <p><b>Input tokens can still be null.</b> `AnthropicOpenAiBridge` now reports both sides —
 * Anthropic sends the prompt side in `message_start`, and the bridge, which emits no such frame,
 * puts it on `message_delta` instead. But a gateway older than that fix, a provider that sends no
 * usage at all, or a stream that dies before the first usage frame all leave it genuinely unknown.
 * It stays null rather than 0 in those cases, because a benchmark that quietly averages an
 * unknown in as free would report a saving that never happened.</p>
 */
export interface TurnUsage {
  /** Null when the provider did not report it — see the remarks. Never coerce this to 0. */
  inputTokens: number | null;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

export interface AssistantTurn {
  content: ContentBlock[];
  stopReason: string | null;
  /**
   * Null when the provider reported nothing — an older gateway, or a stream that failed before
   * `message_start`. Absent rather than zero, so a missing number is never averaged in as free.
   */
  usage: TurnUsage | null;
  /**
   * The model that actually served the turn, as it named itself in `message_start`.
   *
   * <p>Worth capturing because the browser never chooses it: the request carries no `model` and
   * ai-gateway resolves one from the user's and the tenant's settings. So this is the only place
   * the client can learn what it was talking to — and a benchmark that cannot name the model is
   * a benchmark that will eventually compare two of them and call the difference progress.</p>
   */
  model: string | null;
}

/**
 * Thrown when the proxy reports no usable API key for the user or tenant.
 *
 * Carries the provider the server actually resolved, so the panel can name it. Telling somebody
 * with a workspace on OpenAI to "connect your Anthropic account" is worse than saying nothing:
 * they go and create an account they do not need.
 */
export class NoApiKeyError extends Error {
  readonly provider: string | null;

  constructor(provider?: string | null, message?: string) {
    super(message ?? 'No API key is configured for the assistant.');
    this.name = 'NoApiKeyError';
    this.provider = provider ?? null;
  }
}

/**
 * The wall, reached.
 *
 * <p>Its own type because the run must <b>stop</b> rather than retry. A rate limit that the loop
 * treats as an ordinary failure is a loop that keeps asking — which is precisely the runaway the
 * limit exists to end, now with a retry loop bolted on. The runtime checks for this and does not
 * repair past it.</p>
 *
 * <p>`retryAfterSeconds` comes from the server's `Retry-After`, so the panel can say when rather
 * than "later".</p>
 */
export class QuotaError extends Error {
  readonly kind: 'rate_limited' | 'budget_exhausted';
  readonly retryAfterSeconds: number | null;

  constructor(kind: string, message: string, retryAfterSeconds: number | null) {
    super(message);
    this.name = 'QuotaError';
    this.kind = kind === 'budget_exhausted' ? 'budget_exhausted' : 'rate_limited';
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

export interface StreamHandlers {
  onText?: (delta: string) => void;
  onThinking?: (delta: string) => void;
  /** Fires when a tool_use block finishes assembling (name known, input parsed). */
  onToolUse?: (block: ToolUseBlock) => void;
}

/**
 * Run one assistant turn against the proxy, streaming text/thinking deltas to the
 * handlers and returning the fully-assembled content blocks + stop reason.
 */
export async function streamAssistantTurn(
  body: Record<string, unknown>,
  handlers: StreamHandlers,
  signal: AbortSignal,
): Promise<AssistantTurn> {
  const send = async () =>
    fetch(`${base}${MESSAGES_PATH}`, {
      method: 'POST',
      headers: { ...(await adminHeaders()), 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...body, stream: true }),
      signal,
    });

  let res = await send();
  if (res.status === 401 && (await renewSilently())) res = await send();

  if (!res.ok || !res.body) {
    let payload: unknown;
    try {
      payload = await res.json();
    } catch {
      /* non-JSON */
    }
    if ((payload as { error?: string })?.error === 'no_api_key' || res.status === 402) {
      const detail = payload as { provider?: string; message?: string };
      throw new NoApiKeyError(detail?.provider, detail?.message);
    }
    if (res.status === 429) {
      const detail = payload as { error?: string; message?: string };
      const retryAfter = Number(res.headers.get('Retry-After'));
      throw new QuotaError(
        detail?.error ?? 'rate_limited',
        detail?.message ?? 'This workspace has reached its AI usage limit.',
        Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : null,
      );
    }
    const message =
      (payload as { message?: string; error?: string })?.message ??
      (payload as { error?: string })?.error ??
      `AI request failed (${res.status}).`;
    throw new Error(message);
  }

  const blocks: ContentBlock[] = [];
  const jsonBuf: Record<number, string> = {};
  let stopReason: string | null = null;
  let usage: TurnUsage | null = null;
  let model: string | null = null;

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buf = '';

  const handleEvent = (ev: { type?: string; [k: string]: unknown }) => {
    switch (ev.type) {
      case 'content_block_start': {
        const index = ev.index as number;
        blocks[index] = { ...(ev.content_block as ContentBlock) };
        if ((blocks[index] as ToolUseBlock).type === 'tool_use') jsonBuf[index] = '';
        break;
      }
      case 'content_block_delta': {
        const index = ev.index as number;
        const delta = ev.delta as {
          type: string;
          text?: string;
          thinking?: string;
          partial_json?: string;
        };
        const block = blocks[index];
        if (delta.type === 'text_delta' && block?.type === 'text') {
          (block as TextBlock).text += delta.text ?? '';
          handlers.onText?.(delta.text ?? '');
        } else if (delta.type === 'thinking_delta' && block?.type === 'thinking') {
          (block as ThinkingBlock).thinking += delta.thinking ?? '';
          handlers.onThinking?.(delta.thinking ?? '');
        } else if (delta.type === 'input_json_delta') {
          jsonBuf[index] = (jsonBuf[index] ?? '') + (delta.partial_json ?? '');
        }
        break;
      }
      case 'content_block_stop': {
        const index = ev.index as number;
        const block = blocks[index];
        if (block?.type === 'tool_use') {
          const raw = jsonBuf[index];
          (block as ToolUseBlock).input = raw ? safeParse(raw) : {};
          handlers.onToolUse?.(block as ToolUseBlock);
        }
        break;
      }
      case 'message_start': {
        // Anthropic reports the prompt side once, here, and the running completion side in each
        // message_delta below. Both are read with Math.max rather than summed: the delta repeats
        // a running total, so adding them up would inflate the count several-fold.
        const start = ev.message as { usage?: Record<string, number>; model?: string } | undefined;
        if (start?.usage) usage = mergeUsage(usage, start.usage);
        model ??= start?.model ?? null;
        break;
      }
      case 'message_delta':
        stopReason = ((ev.delta as { stop_reason?: string })?.stop_reason ?? stopReason) as
          string | null;
        if (ev.usage) usage = mergeUsage(usage, ev.usage as Record<string, number>);
        break;
      case 'error':
        throw new Error((ev.error as { message?: string })?.message ?? 'Model stream error.');
    }
  };

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += decoder.decode(value, { stream: true });
    let idx: number;
    while ((idx = buf.indexOf('\n\n')) >= 0) {
      const chunk = buf.slice(0, idx);
      buf = buf.slice(idx + 2);
      for (const line of chunk.split('\n')) {
        const m = /^data: ?(.*)$/.exec(line);
        if (!m || !m[1]) continue;
        try {
          handleEvent(JSON.parse(m[1]));
        } catch (e) {
          if (e instanceof Error && e.message !== 'Unexpected end of JSON input') throw e;
        }
      }
    }
  }

  return { content: blocks.filter(Boolean), stopReason, usage, model };
}

/**
 * Folds one provider usage object into the running total for a turn.
 *
 * <p>Every field is a running total on the wire, not an increment, so the highest value seen wins
 * — the same reasoning `AnthropicUsageScanner` uses server-side, and for the same reason: taking
 * a maximum makes a number that arrives twice harmless, where a sum would double it.</p>
 *
 * <p>A field the provider never sends leaves `inputTokens` null rather than 0. See
 * {@link TurnUsage}.</p>
 */
function mergeUsage(current: TurnUsage | null, raw: Record<string, number>): TurnUsage {
  const base: TurnUsage = current ?? {
    inputTokens: null,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
  };
  const num = (v: unknown): number | null => (typeof v === 'number' && v >= 0 ? v : null);
  const input = num(raw.input_tokens);
  return {
    inputTokens: input === null ? base.inputTokens : Math.max(base.inputTokens ?? 0, input),
    outputTokens: Math.max(base.outputTokens, num(raw.output_tokens) ?? 0),
    cacheReadTokens: Math.max(base.cacheReadTokens, num(raw.cache_read_input_tokens) ?? 0),
    cacheCreationTokens: Math.max(
      base.cacheCreationTokens,
      num(raw.cache_creation_input_tokens) ?? 0,
    ),
  };
}

function safeParse(raw: string): Record<string, unknown> {
  try {
    return JSON.parse(raw) as Record<string, unknown>;
  } catch {
    return {};
  }
}
