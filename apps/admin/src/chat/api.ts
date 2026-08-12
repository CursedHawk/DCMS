import { adminHeaders } from '../tenants';

const base = import.meta.env.VITE_ADMIN_API_BASE ?? '/api';

/**
 * Base for the chat SignalR hub (hosted by content-api). Reached same-origin in
 * every deployment — Vite proxies /hub in dev, Caddy routes /hub/* to content-api
 * in prod — so the default is relative. Override with VITE_CONTENT_API_BASE only
 * to point at a content-api on a different origin.
 */
export const contentApiBase = import.meta.env.VITE_CONTENT_API_BASE ?? '';

export interface ChatConversation {
  id: string;
  visitorName: string;
  status: string;
  createdAt: string;
  lastMessageAt: string;
}

export interface ChatMessage {
  id: string;
  sender: 'Visitor' | 'Agent' | 'Bot';
  body: string;
  sentAt: string;
  readAt?: string | null;
}

async function json<T>(path: string): Promise<T> {
  const res = await fetch(`${base}${path}`, { headers: await adminHeaders() });
  if (!res.ok) throw new Error(`GET ${path} → ${res.status}`);
  return res.json() as Promise<T>;
}

export const chatApi = {
  conversations: (status?: string) =>
    json<ChatConversation[]>(`/admin/chat/conversations${status ? `?status=${status}` : ''}`),
  messages: (conversationId: string) =>
    json<ChatMessage[]>(`/admin/chat/conversations/${conversationId}/messages`),
};
