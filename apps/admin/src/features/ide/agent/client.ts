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
  | TextBlock
  | ThinkingBlock
  | ToolUseBlock
  | { type: string; [k: string]: unknown };

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

export interface AssistantTurn {
  content: ContentBlock[];
  stopReason: string | null;
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
    const message =
      (payload as { message?: string; error?: string })?.message ??
      (payload as { error?: string })?.error ??
      `AI request failed (${res.status}).`;
    throw new Error(message);
  }

  const blocks: ContentBlock[] = [];
  const jsonBuf: Record<number, string> = {};
  let stopReason: string | null = null;

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
        const delta = ev.delta as { type: string; text?: string; thinking?: string; partial_json?: string };
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
      case 'message_delta':
        stopReason = ((ev.delta as { stop_reason?: string })?.stop_reason ?? stopReason) as string | null;
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

  return { content: blocks.filter(Boolean), stopReason };
}

function safeParse(raw: string): Record<string, unknown> {
  try {
    return JSON.parse(raw) as Record<string, unknown>;
  } catch {
    return {};
  }
}
