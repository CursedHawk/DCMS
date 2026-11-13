import type { ContentBlock, Message, ToolResultBlock } from '../ide/agent/client';
import { estimateTokens } from './contracts';

/**
 * Sizing and checking a conversation.
 *
 * <p>This used to be where conversations were pruned: old tool results blanked, whole exchanges
 * dropped from the front, a few remembered facts prepended. That lost parts of the conversation
 * outright and rewrote its front at every cut. Conversations are now compacted whole instead, into
 * one summary they continue from (`compaction.ts`, ADR 0034); what is left here is the estimate
 * the compaction trigger falls back on and the structural check every request must pass.</p>
 */

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
