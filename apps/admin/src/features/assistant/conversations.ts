import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api';
import type { ContentBlock, Message, ToolResultBlock } from '../ide/agent/client';
import type { AiMode } from './modes';

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

export interface ConversationSummary {
  id: string;
  title: string;
  visibility: Visibility;
  mode: string;
  pageArea: string | null;
  messageCount: number;
  ownerUserId: string;
  /** Whether the signed-in member owns it — only they may continue or rename it. */
  mine: boolean;
  createdAt: string;
  updatedAt: string;
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
}

const KEY = 'ai-conversations';

export function useConversations(scope: ConversationScope, enabled = true) {
  return useQuery({
    queryKey: [KEY, scope],
    enabled,
    queryFn: () => api.get<ConversationSummary[]>(`/admin/ai/conversations?scope=${scope}`),
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
    mutationFn: (body: { title: string; mode: AiMode; pageArea?: string | null }) =>
      api.post<{ id: string; title: string }>('/admin/ai/conversations', body),
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
  extra?: { title?: string; mode?: AiMode },
): Promise<{ messageCount: number }> {
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

export const conversationsKey = KEY;
