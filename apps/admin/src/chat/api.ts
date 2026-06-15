import { adminHeaders } from '../tenants';

const base = import.meta.env.VITE_ADMIN_API_BASE ?? '/api';

/** Origin of content-api, which hosts the chat SignalR hub. */
export const contentApiBase = import.meta.env.VITE_CONTENT_API_BASE ?? 'http://localhost:5003';

export interface ChatConversation {
  id: string;
  visitorName: string;
  status: string;
  createdAt: string;
  lastMessageAt: string;
}

export interface ChatMessage {
  id: string;
  sender: 'Visitor' | 'Agent';
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
