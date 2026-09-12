import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api';
import type { ContentBlock, Message, ToolResultBlock } from '../ide/agent/client';
import type { AiMode } from '../agent/modes';

/**
 * Stored conversations.
 *
 * <p>The transcript is kept on the server rather than in the browser because it is not really
 * chat: it is the record of an agent writing to a workspace, and it has to survive a reload,
 * follow the operator to another machine, and — where they choose to share it — be readable by
 * the colleague who inherits whatever it did.</p>
 */

export type ConversationScope = 'mine' | 'workspace' | 'all';

export type Visibility = 'Private' | 'Workspace';

/**
 * Which surface a conversation belongs to.
 *
 * <p>One table, filtered — see `AiConversation.Surface` for why that beat a second table. What
 * it costs is this: <b>every list call must name a surface</b>, or the IDE's many short runs and
 * the console's few long conversations end up in one list where the second is invisible.</p>
 */
export type AiSurface = 'console' | 'ide';

export interface ConversationSummary {
  id: string;
  title: string;
  visibility: Visibility;
  mode: string;
  pageArea: string | null;
  surface: AiSurface;
  /** The site an IDE conversation is about. Null on the console surface. */
  siteId: string | null;
  /** The branch it was last run against — last, not first: an operator can switch mid-run. */
  branch: string | null;
  messageCount: number;
  ownerUserId: string;
  /** Whether the signed-in member owns it — only they may continue or rename it. */
  mine: boolean;
  createdAt: string;
  updatedAt: string;
}

/**
 * One task, however many turns it took.
 *
 * <p>Deliberately not a message: the outcome, the classification and whether the build gate
 * passed are things the loop knows and never says to the model, and a run record in the message
 * array would corrupt what gets handed back on resume.</p>
 *
 * <p>The diff is not here either, because it is already in the transcript — the `tool_use` block
 * for an edit carries its own anchor and replacement. What this adds is the summary across the
 * whole run, which no single message has.</p>
 */
export interface RunRecord {
  id: string;
  task: string;
  fromSeq: number;
  toSeq: number;
  outcome: 'completed' | 'failed' | 'stopped';
  complexity: string | null;
  changes: { path: string; kind: string }[];
  /** Null means never checked — the preview was closed. Not the same as failing. */
  validation: { ok: boolean; report: string } | null;
  metrics: Record<string, unknown> | null;
  startedAt: string;
  /** Null for a run whose tab was closed before it finished. That is the honest record. */
  finishedAt: string | null;
}

export interface StoredMessage {
  id: string;
  seq: number;
  role: 'user' | 'assistant';
  content: ContentBlock[] | ToolResultBlock[];
  createdAt: string;
}

export interface ConversationDetail extends ConversationSummary {
  messages: StoredMessage[];
  runs: RunRecord[];
}

const KEY = 'ai-conversations';

/**
 * List conversations on one surface.
 *
 * <p>`surface` is required rather than defaulted at the call site, because forgetting it is the
 * one mistake that makes a shared table look like a bug: the console rail would fill with IDE
 * runs and nobody would find the conversation they were looking for.</p>
 */
export function useConversations(
  scope: ConversationScope,
  surface: AiSurface,
  options: { siteId?: string; enabled?: boolean } = {},
) {
  const { siteId, enabled = true } = options;
  return useQuery({
    queryKey: [KEY, scope, surface, siteId ?? null],
    enabled,
    queryFn: () => {
      const qs = new URLSearchParams({ scope, surface });
      if (siteId) qs.set('siteId', siteId);
      return api.get<ConversationSummary[]>(`/admin/ai/conversations?${qs}`);
    },
  });
}

export function useConversation(id: string | null) {
  return useQuery({
    queryKey: [KEY, 'detail', id],
    enabled: !!id,
    // A stored conversation only changes when this browser appends to it, and the append
    // invalidates the key itself — refetching on every window focus would replace a live
    // transcript mid-run with the last saved copy of it.
    staleTime: Infinity,
    queryFn: () => api.get<ConversationDetail>(`/admin/ai/conversations/${id}`),
  });
}

export function useCreateConversation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: {
      title: string;
      mode: AiMode;
      pageArea?: string | null;
      surface?: AiSurface;
      siteId?: string | null;
      branch?: string | null;
    }) => api.post<{ id: string; title: string }>('/admin/ai/conversations', body),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: [KEY] }),
  });
}

export function useUpdateConversation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      id,
      ...body
    }: {
      id: string;
      title?: string;
      visibility?: Visibility;
      mode?: AiMode;
      archived?: boolean;
    }) => api.patch<ConversationSummary>(`/admin/ai/conversations/${id}`, body),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: [KEY] }),
  });
}

export function useDeleteConversation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.del<void>(`/admin/ai/conversations/${id}`),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: [KEY] }),
  });
}

/**
 * Append the turns produced since the last save.
 *
 * <p>Called at turn boundaries rather than per streamed token: a conversation is only worth
 * storing once the model has finished saying something, and a write per delta would put a
 * request on the network for every few characters.</p>
 */
export function appendMessages(
  id: string,
  messages: readonly Message[],
  extra?: { title?: string; mode?: AiMode; branch?: string },
): Promise<{ messageCount: number; lastSeq: number }> {
  return api.post(`/admin/ai/conversations/${id}/messages`, {
    messages: messages.map((message) => ({
      role: message.role,
      content:
        typeof message.content === 'string'
          ? [{ type: 'text', text: message.content }]
          : message.content,
    })),
    ...extra,
  });
}

/**
 * A title from the opening question.
 *
 * <p>Generated here rather than asked of the model: naming the conversation is not worth a
 * round trip the operator waits for, and the first thing they typed is what they will recognise
 * it by in the rail.</p>
 */
export function titleFrom(question: string): string {
  const line = question.trim().split('\n')[0] ?? '';
  const clean = line.replace(/\s+/g, ' ').trim();
  if (clean.length <= 60) return clean || 'New conversation';
  // Cut at a word so the rail does not show half of one.
  const cut = clean.slice(0, 60);
  const space = cut.lastIndexOf(' ');
  return `${(space > 30 ? cut.slice(0, space) : cut).trim()}…`;
}

/**
 * Record a run's start, and later its end.
 *
 * <p><b>Upserted on an id the browser generates, because a run has two halves and the tab can
 * die between them.</b> The loop runs in the browser (D1), so nobody writes a closing record for
 * a run whose tab was closed — and writing only at the end would mean the stored history held
 * successes and nothing else, when the runs worth reviewing are exactly the ones that stopped.
 * A run left with `finishedAt: null` is not a gap in the data; it is the record of a run that
 * never finished.</p>
 *
 * <p>Never throws. A history that failed to record must not take down the run it describes.</p>
 */
export async function recordRun(
  conversationId: string,
  runId: string,
  body: {
    task?: string;
    fromSeq?: number;
    toSeq?: number;
    outcome?: string;
    complexity?: string | null;
    changes?: { path: string; kind: string }[];
    validation?: { ok: boolean; report: string } | null;
    metrics?: Record<string, unknown> | null;
    finished?: boolean;
  },
): Promise<void> {
  try {
    await api.put<{ id: string }>(
      `/admin/ai/conversations/${conversationId}/runs/${runId}`,
      body,
    );
  } catch {
    // Deliberately silent. The transcript is the record that matters and it is written
    // separately; losing the summary on top of it is not worth an error in the operator's face.
  }
}

export const conversationsKey = KEY;
