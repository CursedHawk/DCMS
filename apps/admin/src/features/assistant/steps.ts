import type { ContentBlock, Message, ToolResultBlock, ToolUseBlock } from '../ide/agent/client';
import type { AssistantTool, ToolRisk } from './tools';

/**
 * What the transcript is made of.
 *
 * <p>It used to be `{ type, text }` — one flat string per event — which is why a run of twelve
 * tool calls read as twelve lines of `update_content` and told the operator nothing about what
 * had happened to their workspace. A step keeps the call, its arguments, its result and its
 * timing, so the transcript can show one legible line and open into the detail behind it.</p>
 */
export type StepStatus = 'awaiting' | 'running' | 'ok' | 'error' | 'declined';

export interface UserStep {
  kind: 'user';
  id: string;
  text: string;
  /** Names only. The bytes stay in the attachment pool; a transcript is not a file store. */
  files?: readonly string[];
}

export interface SayStep {
  kind: 'assistant';
  id: string;
  text: string;
}

export interface ToolStep {
  kind: 'tool';
  id: string;
  /** The model's own id for the call, which is how a result is matched back to it. */
  callId: string;
  name: string;
  /** What the card says: "Created draft autumn-26". Falls back to the tool name. */
  label: string;
  risk: ToolRisk | 'read';
  input: Record<string, unknown>;
  status: StepStatus;
  /** The raw result string the model was given, if it got one. */
  result?: string;
  error?: string;
  startedAt?: number;
  endedAt?: number;
}

export interface ErrorStep {
  kind: 'error';
  id: string;
  text: string;
}

export type Step = UserStep | SayStep | ToolStep | ErrorStep;

/** Fields a work card can show a before/after for, when the tool returned one. */
export interface FieldChange {
  field: string;
  before: unknown;
  after: unknown;
}

/**
 * The before/after pairs inside a tool result, if it carries any.
 *
 * <p>Only `update_content` produces these today, and it does so because it has already read the
 * old draft in order to merge. Reading them here rather than assuming the shape means a tool
 * that never learns to report changes simply shows no diff, instead of showing a wrong one.</p>
 */
export function fieldChanges(step: ToolStep): FieldChange[] {
  if (!step.result) return [];
  try {
    const parsed = JSON.parse(step.result) as {
      changed?: { before?: Record<string, unknown>; after?: Record<string, unknown> };
    };
    const before = parsed.changed?.before;
    const after = parsed.changed?.after;
    if (!before || !after) return [];
    return Object.keys(after).map((field) => ({
      field,
      before: before[field] ?? null,
      after: after[field],
    }));
  } catch {
    // A result that is not JSON is not a defect: several tools return prose on failure.
    return [];
  }
}

/** Pretty JSON when it is JSON, the string itself when it is not. */
export function prettyPayload(value: string | undefined): string {
  if (!value) return '';
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

const id = () => crypto.randomUUID();

export const userStep = (text: string, files?: readonly string[]): UserStep => ({
  kind: 'user',
  id: id(),
  text,
  files: files?.length ? files : undefined,
});

export const sayStep = (text: string): SayStep => ({ kind: 'assistant', id: id(), text });

export const errorStep = (text: string): ErrorStep => ({ kind: 'error', id: id(), text });

export function toolStep(
  call: ToolUseBlock,
  tool: AssistantTool | undefined,
  status: StepStatus,
): ToolStep {
  const input = (call.input ?? {}) as Record<string, unknown>;
  return {
    kind: 'tool',
    id: id(),
    callId: call.id,
    name: call.name,
    label: describe(tool, input) ?? call.name,
    risk: tool?.risk ?? 'read',
    input,
    status,
    startedAt: Date.now(),
  };
}

function describe(
  tool: AssistantTool | undefined,
  input: Record<string, unknown>,
): string | undefined {
  try {
    return tool?.describe?.(input);
  } catch {
    // A describe() that throws on odd arguments must not take the transcript with it.
    return undefined;
  }
}

/**
 * Rebuild a transcript from stored turns.
 *
 * <p>The stored messages are the model's own content blocks, so this is a projection rather
 * than a second source of truth: a `tool_use` block becomes a card and the matching
 * `tool_result` fills in how it went. A card whose result never arrived — the tab was closed
 * mid-run — is shown as still running rather than quietly dropped, because "we stopped
 * recording here" is the honest reading of it.</p>
 */
export function stepsFromMessages(
  messages: readonly Message[],
  tools: readonly AssistantTool[],
): Step[] {
  const steps: Step[] = [];
  const cards = new Map<string, ToolStep>();

  for (const message of messages) {
    const blocks: ContentBlock[] =
      typeof message.content === 'string'
        ? [{ type: 'text', text: message.content }]
        : (message.content as ContentBlock[]);

    for (const block of blocks) {
      if (block.type === 'text') {
        const text = String((block as { text?: unknown }).text ?? '').trim();
        if (!text) continue;
        steps.push(message.role === 'user' ? userStep(text) : sayStep(text));
        continue;
      }

      if (block.type === 'tool_use') {
        const call = block as ToolUseBlock;
        const card = toolStep(
          call,
          tools.find((t) => t.name === call.name),
          'running',
        );
        cards.set(call.id, card);
        steps.push(card);
        continue;
      }

      if (block.type === 'tool_result') {
        const result = block as unknown as ToolResultBlock;
        const card = cards.get(result.tool_use_id);
        if (!card) continue;
        card.status = result.is_error ? 'error' : 'ok';
        if (result.is_error) card.error = String(result.content);
        else card.result = String(result.content);
        card.endedAt = card.startedAt;
      }
    }
  }

  return steps;
}
