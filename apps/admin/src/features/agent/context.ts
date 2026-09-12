import type { ContentBlock, Message, ToolResultBlock } from '../ide/agent/client';
import { estimateTokens, type ContextBudget } from './contracts';

/**
 * Keeps a conversation inside its budget without breaking it.
 *
 * <p>A long run's history is dominated by <b>old tool results</b>: three file reads from eight
 * turns ago are thousands of tokens describing a version of the code that no longer exists, resent
 * on every subsequent turn. The model almost never needs them again, and when it does it can read
 * the file — which is cheap, current, and something it is already good at.</p>
 *
 * <p>So compression works outside-in: elide the oldest tool results first, then drop whole
 * exchanges, and only ever summarise as a last resort. Recent turns stay verbatim because that is
 * where the actual reasoning is.</p>
 *
 * <h3>The constraint that shapes all of this</h3>
 * <p>The Messages API requires every `tool_use` block to be answered by a `tool_result` with the
 * matching id, in the next message. A compressor that drops one and not the other produces a
 * request the provider rejects outright — so messages are only ever removed in whole exchanges
 * from the front, and eliding replaces a result's <i>text</i> while leaving the block in place.</p>
 */

/**
 * The structured carry-over that survives compression.
 *
 * <p>This is the brief's "working memory": the handful of facts a run must not forget even after
 * the turns that established them have been elided. It is maintained by the runtime rather than
 * asked of the model, because a model asked to summarise its own history spends a turn doing it.</p>
 */
export interface WorkingMemory {
  goal: string;
  /** Paths the run has written, in first-touched order. */
  filesChanged: string[];
  /** Notes the runtime knows for certain — a failed build, a declined tool. */
  notes: string[];
}

export interface PruneResult {
  messages: Message[];
  /** How many tool results had their content replaced by a stub. */
  elided: number;
  /** How many whole exchanges were dropped from the front. */
  dropped: number;
  /** Estimated tokens after pruning. */
  estimated: number;
}

/** What an elided tool result says instead of its content. */
const ELIDED = '[earlier result omitted to save context — re-run the tool if you need it again]';

/**
 * Exchanges at the end of the conversation that are never touched.
 *
 * <p>Four is two full tool round trips. Less than that and a run loses the result it is
 * currently reasoning about, which is the one thing compression must never do.</p>
 */
const KEEP_VERBATIM = 4;

export function estimateMessages(messages: readonly Message[]): number {
  let total = 0;
  for (const message of messages) {
    if (typeof message.content === 'string') {
      total += estimateTokens(message.content);
      continue;
    }
    for (const block of message.content as ContentBlock[]) {
      total += estimateTokens(blockText(block));
    }
  }
  return total;
}

function blockText(block: ContentBlock): string {
  if (block.type === 'text') return String((block as { text?: string }).text ?? '');
  if (block.type === 'thinking') return String((block as { thinking?: string }).thinking ?? '');
  if (block.type === 'tool_result') return String((block as { content?: string }).content ?? '');
  if (block.type === 'tool_use') return JSON.stringify((block as { input?: unknown }).input ?? {});
  return '';
}

/**
 * Bring a conversation within budget, least-destructive step first.
 *
 * <p>Returns a new array; the caller's history is not mutated, so a compressed request can be
 * sent while the full transcript is still what gets stored.</p>
 */
export function pruneHistory(
  messages: readonly Message[],
  budget: ContextBudget,
  memory?: WorkingMemory,
): PruneResult {
  let working = messages.map(cloneMessage);
  let elided = 0;
  let dropped = 0;

  const withinBudget = () => estimateMessages(working) <= budget.maxInputTokens;
  if (withinBudget()) {
    return { messages: working, elided, dropped, estimated: estimateMessages(working) };
  }

  // Step 1 — elide old tool results, oldest first. Cheapest and least lossy: the content is
  // recoverable by re-running the tool, and usually describes code that has since changed.
  const protectedFrom = Math.max(0, working.length - KEEP_VERBATIM);
  for (let i = 0; i < protectedFrom && !withinBudget(); i++) {
    const message = working[i];
    if (typeof message.content === 'string' || !Array.isArray(message.content)) continue;

    let changed = false;
    message.content = (message.content as ContentBlock[]).map((block) => {
      if (block.type !== 'tool_result') return block;
      const result = block as unknown as ToolResultBlock;
      if (result.content === ELIDED) return block;
      changed = true;
      return { ...result, content: ELIDED } as unknown as ContentBlock;
    }) as Message['content'];
    if (changed) elided++;
  }

  // Step 2 — drop whole exchanges from the front.
  //
  // In PAIRS, because an assistant message carrying tool_use must keep the user message
  // carrying its tool_result: orphaning either is a request the provider refuses outright.
  while (!withinBudget() && working.length > KEEP_VERBATIM + 1) {
    const removed = dropLeadingExchange(working);
    if (removed === 0) break;
    working = working.slice(removed);
    dropped++;
  }

  // Step 3 — prepend what was lost, so the run does not forget its own goal.
  if ((dropped > 0 || elided > 0) && memory) {
    working = [{ role: 'user', content: renderMemory(memory) }, ...working];
  }

  return { messages: working, elided, dropped, estimated: estimateMessages(working) };
}

/**
 * How many messages to remove to drop one complete exchange from the front.
 *
 * <p>An exchange is a user message, the assistant reply, and — if that reply asked for tools —
 * the user message carrying the results. Returns 0 when the front cannot be safely trimmed.</p>
 */
function dropLeadingExchange(messages: readonly Message[]): number {
  if (messages.length < 2) return 0;

  let count = 1; // the leading user message
  const assistant = messages[1];
  if (assistant?.role !== 'assistant') return count;
  count++;

  // If that assistant turn used tools, its results are the next message and must go with it.
  const usedTools =
    Array.isArray(assistant.content) &&
    (assistant.content as ContentBlock[]).some((b) => b.type === 'tool_use');
  if (usedTools && messages[2]?.role === 'user') count++;

  return count;
}

function renderMemory(memory: WorkingMemory): string {
  const parts = [`[Earlier turns were compressed. Carrying forward:]`, `Goal: ${memory.goal}`];
  if (memory.filesChanged.length > 0) {
    parts.push(`Files changed so far: ${memory.filesChanged.join(', ')}`);
  }
  for (const note of memory.notes) parts.push(note);
  return parts.join('\n');
}

function cloneMessage(message: Message): Message {
  return {
    role: message.role,
    content: Array.isArray(message.content)
      ? (message.content as ContentBlock[]).map((b) => ({ ...b }))
      : message.content,
  } as Message;
}

/**
 * Check that a conversation is structurally valid for the Messages API.
 *
 * <p>Exported because it is the assertion the compressor's tests are built on: every `tool_use`
 * answered by a `tool_result` with its id, in the immediately following message. Getting this
 * wrong produces a 400 from the provider at runtime, which is a long way from the code that
 * caused it.</p>
 */
export function findOrphanedToolUses(messages: readonly Message[]): string[] {
  const orphans: string[] = [];
  for (let i = 0; i < messages.length; i++) {
    const content = messages[i].content;
    if (!Array.isArray(content)) continue;

    const uses = (content as ContentBlock[]).filter((b) => b.type === 'tool_use');
    if (uses.length === 0) continue;

    const next = messages[i + 1]?.content;
    const answered = new Set(
      Array.isArray(next)
        ? (next as ContentBlock[])
            .filter((b) => b.type === 'tool_result')
            .map((b) => String((b as unknown as ToolResultBlock).tool_use_id))
        : [],
    );

    for (const use of uses) {
      const id = String((use as { id?: string }).id ?? '');
      if (!answered.has(id)) orphans.push(id);
    }
  }
  return orphans;
}
