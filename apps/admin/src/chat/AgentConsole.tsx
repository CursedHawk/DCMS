import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { useQuery } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { getAccessToken } from '../auth';
import { getCurrentTenantSlug } from '../tenants';
import { chatApi, contentApiBase, type ChatConversation, type ChatMessage } from './api';

/**
 * Agent console: lists conversations (from admin-api) and joins the live
 * content-api hub to send/receive in realtime. The hub authenticates the agent
 * with the platform access token (passed via the access_token query string, the
 * only way to carry credentials over the WebSocket transport) and the tenant slug.
 */
export function AgentConsole() {
  const slug = getCurrentTenantSlug();
  const [connState, setConnState] = useState<HubConnectionState>(HubConnectionState.Disconnected);
  const [selected, setSelected] = useState<string | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [draft, setDraft] = useState('');
  const connRef = useRef<HubConnection | null>(null);

  const conversations = useQuery({
    queryKey: ['chat-conversations'],
    queryFn: () => chatApi.conversations(),
    refetchInterval: 15_000,
  });

  // Establish the hub connection once per tenant.
  useEffect(() => {
    if (!slug) return;
    let disposed = false;
    const connection = new HubConnectionBuilder()
      .withUrl(`${contentApiBase}/hub/chat?tenant=${encodeURIComponent(slug)}`, {
        accessTokenFactory: async () => (await getAccessToken()) ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('ReceiveMessage', (m: ChatMessage & { conversationId: string }) => {
      setSelected((current) => {
        if (current === m.conversationId) {
          setMessages((prev) => [...prev, m]);
        }
        return current;
      });
    });
    connection.on('ConversationStarted', () => void conversations.refetch());
    connection.on('ConversationActivity', () => void conversations.refetch());

    connection.onreconnected(() => setConnState(connection.state));
    connection.onclose(() => setConnState(HubConnectionState.Disconnected));

    connRef.current = connection;
    connection
      .start()
      .then(() => !disposed && setConnState(connection.state))
      .catch(() => setConnState(HubConnectionState.Disconnected));

    return () => {
      disposed = true;
      void connection.stop();
      connRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [slug]);

  const openConversation = useCallback(async (id: string) => {
    setSelected(id);
    setMessages(await chatApi.messages(id));
    const conn = connRef.current;
    if (conn?.state === HubConnectionState.Connected) {
      await conn.invoke('JoinConversation', id);
    }
  }, []);

  async function send() {
    const conn = connRef.current;
    if (!selected || !draft.trim() || conn?.state !== HubConnectionState.Connected) return;
    await conn.invoke('SendAgentMessage', selected, draft.trim());
    setDraft('');
  }

  if (!slug) {
    return <p className="text-slate-600">Select a tenant to open the chat console.</p>;
  }

  return (
    <section>
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Live chat</h1>
        <span className="text-xs text-slate-500">
          {connState === HubConnectionState.Connected ? '● connected' : '○ offline'}
        </span>
      </div>
      <div className="mt-4 grid grid-cols-[280px_1fr] gap-4">
        <aside className="rounded border border-slate-200 bg-white">
          <ul className="divide-y divide-slate-100">
            {(conversations.data ?? []).map((c: ChatConversation) => (
              <li key={c.id}>
                <button
                  type="button"
                  onClick={() => void openConversation(c.id)}
                  className={`flex w-full flex-col items-start px-3 py-2 text-left text-sm hover:bg-slate-50 ${
                    selected === c.id ? 'bg-slate-100' : ''
                  }`}
                >
                  <span className="font-medium">{c.visitorName}</span>
                  <span className="text-xs text-slate-400">
                    {c.status} · {new Date(c.lastMessageAt).toLocaleTimeString()}
                  </span>
                </button>
              </li>
            ))}
            {conversations.data?.length === 0 && (
              <li className="px-3 py-4 text-sm text-slate-400">No conversations yet.</li>
            )}
          </ul>
        </aside>

        <div className="flex h-[60vh] flex-col rounded border border-slate-200 bg-white">
          {selected ? (
            <>
              <div className="flex-1 space-y-2 overflow-y-auto p-3">
                {messages.map((m) => (
                  <div
                    key={m.id}
                    className={`max-w-[70%] rounded px-3 py-2 text-sm ${
                      m.sender === 'Agent'
                        ? 'ml-auto bg-blue-600 text-white'
                        : 'bg-slate-100 text-slate-900'
                    }`}
                  >
                    {m.body}
                  </div>
                ))}
              </div>
              <div className="flex gap-2 border-t border-slate-100 p-2">
                <input
                  value={draft}
                  onChange={(e) => setDraft(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && void send()}
                  placeholder="Type a reply…"
                  className="flex-1 rounded border border-slate-300 px-2 py-1 text-sm"
                />
                <button type="button" onClick={() => void send()} className="rounded bg-slate-900 px-3 py-1 text-sm text-white">
                  Send
                </button>
              </div>
            </>
          ) : (
            <p className="m-auto text-sm text-slate-400">Select a conversation.</p>
          )}
        </div>
      </div>
    </section>
  );
}
