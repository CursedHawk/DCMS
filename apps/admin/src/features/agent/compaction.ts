import { aiPrompt, type AiPromptOverrides } from '@dcms/core';
import type { AssistantTurn, ContentBlock, Message, StreamHandlers, TurnUsage } from '../ide/agent/client';
import { estimateMessages } from './context';
import { estimateTokens } from './contracts';
import { knowledgeApi, normalisePath } from './knowledge';
import { withCacheHints } from './modelRouter';

/**
 * Whole-conversation compaction (ADR 0034).
 *
 * <p>When a conversation's prompt reaches the tenant's threshold, the model summarizes all of it
 * and the conversation continues from that summary alone: no earlier turn and no earlier thinking
 * block is sent again. That is the API's recommended "simple compaction" shape, and it is the one
 * that survives preserved thinking's history check — nothing replayed is bound to the history it
 * replaced — while keeping every request after it append-only, so the cache warms on the new
 * prefix.</p>
 *
 * <p>The summarization request is the previous request with one message appended (the
 * compaction prompt), so it is itself a cache read. The transcript stays append-only too: the
 * summary is stored as a message of kind `compaction`, and what is sent starts there.</p>
 *
 * <p>It runs only at a model-call boundary, where every `tool_use` has its result — never in the
 * middle of a tool round.</p>
 */

/** The first line of a continuation, which the transcript recognises as a divider. */
export const COMPACTION_HEADER = '[This conversation was compacted. Everything before this point is summarized below.]';

/** What the transport needs: the same one the loops use. */
export type Transport = (body: Record<string, unknown>, handlers: StreamHandlers, signal: AbortSignal) => Promise<AssistantTurn>;

/** One lasting fact the summarizer pulled out of the conversation. */
export interface KnowledgeItem {
  path: string;
  title: string;
  description: string;
  body: string;
}

/** Where the model's context starts: the last compaction, or the beginning. */
export function contextStart(messages: readonly Message[]): number {
  for (let i = messages.length - 1; i >= 0; i--) if (messages[i].kind === 'compaction') return i;
  return 0;
}

/** A message as the API takes it: role and content, nothing of ours. */
export function toWire(message: Message): Message {
  return { role: message.role, content: message.content };
}

/** What the model is sent: the transcript from its last compaction on. */
export function contextOf(messages: readonly Message[]): Message[] {
  return messages.slice(contextStart(messages)).map(toWire);
}

/**
 * How big the next prompt will be.
 *
 * <p>From the provider's own count of the last request when there is one — input, cache reads
 * and cache writes, plus what it answered — and an estimate only of what was appended since.
 * Without a count (a provider that reports none), an estimate of the whole request.</p>
 */
export function promptTokens(input: {
  lastUsage: TurnUsage | null;
  /** Messages appended after the turn `lastUsage` describes. */
  appended: readonly Message[];
  /** The context as it would be sent, for the fallback. */
  context: readonly Message[];
  system: string;
  tools: readonly unknown[];
}): number {
  const usage = input.lastUsage;
  if (usage && usage.inputTokens !== null) {
    return (
      usage.inputTokens + usage.cacheReadTokens + usage.cacheCreationTokens + usage.outputTokens + estimateMessages(input.appended)
    );
  }
  return estimateMessages(input.context) + estimateTokens(input.system) + estimateTokens(JSON.stringify(input.tools));
}

/**
 * Ask the model for a summary of the whole context, and the lasting facts in it.
 *
 * <p>`system`, `tools` and `context` must be exactly what the loop last sent, so the request is a
 * cache read. A failure throws: a run that cannot compact must say so rather than trim silently.</p>
 */
export async function summarize(opts: {
  system: unknown;
  tools: readonly unknown[];
  context: readonly Message[];
  /** Request parameters the loop sends besides system, tools and messages (model, thinking, effort). */
  params?: Record<string, unknown>;
  overrides?: AiPromptOverrides | null;
  transport: Transport;
  signal: AbortSignal;
}): Promise<{ summary: string; knowledge: KnowledgeItem[] }> {
  const turn = await opts.transport(
    withCacheHints({
      max_tokens: 16_000,
      ...opts.params,
      system: opts.system,
      ...(opts.tools.length > 0 ? { tools: opts.tools } : {}),
      messages: [...opts.context.map(toWire), { role: 'user', content: aiPrompt('compaction', opts.overrides) }],
    }),
    {},
    opts.signal,
  );
  const text = (turn.content as ContentBlock[])
    .filter((block) => block.type === 'text')
    .map((block) => String((block as { text?: string }).text ?? ''))
    .join('')
    .trim();
  if (!text) throw new Error('The conversation could not be compacted: the summary came back empty.');
  return parseSummary(text);
}

/** The summary and knowledge out of the model's answer; the whole answer when it ignored the tags. */
export function parseSummary(text: string): { summary: string; knowledge: KnowledgeItem[] } {
  const summary = /<summary>([\s\S]*?)<\/summary>/i.exec(text)?.[1]?.trim() ?? text.replace(/<knowledge>[\s\S]*?<\/knowledge>/i, '').trim();
  let knowledge: KnowledgeItem[] = [];
  const raw = /<knowledge>([\s\S]*?)<\/knowledge>/i.exec(text)?.[1];
  if (raw) {
    try {
      const parsed = JSON.parse(raw.trim()) as unknown;
      if (Array.isArray(parsed)) {
        knowledge = parsed
          .filter((item): item is KnowledgeItem =>
            !!item &&
            typeof item === 'object' &&
            ['path', 'title', 'description', 'body'].every((k) => typeof (item as Record<string, unknown>)[k] === 'string'),
          )
          .map((item) => ({ ...item, path: normalisePath(item.path) }))
          .filter((item) => item.path.includes('/'));
      }
    } catch {
      // Unreadable knowledge is no knowledge: the summary is what the conversation needs.
    }
  }
  return { summary, knowledge };
}

/**
 * Save the compacted part of a conversation, and what was learnt in it, to the knowledge store.
 *
 * <p>The archive is `conversations/<date>-<title>-<id>/part-<n>`: the summary, with the
 * conversation and seq range it covers, so `conversation_recall` can look into the turns it
 * summarized. Best effort — a member who may not write knowledge still gets their conversation
 * compacted — and it says what it saved, for the continuation to reference.</p>
 */
export async function archive(opts: {
  /** The site (or repository) a site agent works on; null for the assistant (workspace-wide). */
  siteId: string | null;
  conversationId: string | null;
  title: string;
  /** Which compaction of this conversation this is, from 1. */
  part: number;
  fromSeq: number | null;
  toSeq: number | null;
  summary: string;
  knowledge: readonly KnowledgeItem[];
  /** Whether the member may write knowledge; nothing is attempted when they may not. */
  mayWrite: boolean;
}): Promise<{ archivedAt: string | null; saved: string[] }> {
  if (!opts.mayWrite) return { archivedAt: null, saved: [] };
  const folder = `conversations/${conversationFolder(opts.title, opts.conversationId)}`;
  const path = `${folder}/part-${opts.part}`;
  let archivedAt: string | null = null;
  try {
    await knowledgeApi.save({
      siteId: opts.siteId,
      path,
      title: `${opts.title} — part ${opts.part}`,
      description: `Summary of part ${opts.part} of the conversation “${opts.title}”, archived when it was compacted.`,
      body: opts.summary,
      source: 'compaction',
      conversationId: opts.conversationId,
      fromSeq: opts.fromSeq,
      toSeq: opts.toSeq,
    });
    archivedAt = path;
  } catch {
    // Archiving is a convenience: the conversation goes on without it.
  }
  const saved: string[] = [];
  for (const item of opts.knowledge) {
    try {
      // Not linked to the conversation: a decision is the workspace's to know, as when an agent
      // saves one itself. The summary above is linked, and as private as the conversation.
      await knowledgeApi.save({ siteId: opts.siteId, ...item, source: 'compaction' });
      saved.push(item.path);
    } catch {
      // As above, per entry.
    }
  }
  return { archivedAt, saved };
}

/** `2026-11-13-hr-portal-1a2b3c4d`: findable by date and title, unique by the conversation. */
export function conversationFolder(title: string, conversationId: string | null, now = new Date()): string {
  const slug = normalisePath(title.replace(/\//g, ' ')).slice(0, 40).replace(/-$/, '') || 'conversation';
  const id = (conversationId ?? crypto.randomUUID()).replace(/-/g, '').slice(0, 8);
  return `${now.toISOString().slice(0, 10)}-${slug}-${id}`;
}

/** The message a conversation continues from after compaction. */
export function continuation(opts: { summary: string; archivedAt: string | null; saved: readonly string[] }): Message {
  const parts = [COMPACTION_HEADER, '', opts.summary];
  if (opts.archivedAt) {
    parts.push(
      '',
      `The conversation up to this point is archived in the knowledge store at ${opts.archivedAt}. When you need a detail this summary left out, look it up with conversation_recall on that path.`,
    );
  }
  if (opts.saved.length > 0) parts.push('', `Saved to the knowledge store from it: ${opts.saved.join(', ')}.`);
  parts.push('', 'Continue from where the conversation left off.');
  return { role: 'user', kind: 'compaction', content: [{ type: 'text', text: parts.join('\n') }] as ContentBlock[] };
}

/** How many times a conversation has been compacted so far. */
export function compactions(messages: readonly Message[]): number {
  return messages.filter((m) => m.kind === 'compaction').length;
}
