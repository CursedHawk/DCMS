import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { useQuery } from '@tanstack/react-query';
import { Bot, MessagesSquare, Send } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { EmptyState } from '../../components/ui/empty-state';
import { Input } from '../../components/ui/input';
import { cn } from '../../lib/cn';
import { getAccessToken } from '../../auth';
import { getCurrentTenantSlug } from '../../tenants';
import { type ChatMessage, chatApi, contentApiBase } from '../../chat/api';

export function ChatPage() {
  const { t } = useTranslation();
  const slug = getCurrentTenantSlug();
  const [connected, setConnected] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [draft, setDraft] = useState('');
  const connRef = useRef<HubConnection | null>(null);
  const endRef = useRef<HTMLDivElement>(null);

  const conversations = useQuery({
    queryKey: ['chat-conversations'],
    queryFn: () => chatApi.conversations(),
    refetchInterval: 15_000,
  });

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
        if (current === m.conversationId) setMessages((prev) => [...prev, m]);
        return current;
      });
    });
    connection.on('ConversationStarted', () => void conversations.refetch());
    connection.on('ConversationActivity', () => void conversations.refetch());
    connection.onreconnected(() => setConnected(true));
    connection.onclose(() => setConnected(false));

    connRef.current = connection;
    connection
      .start()
      .then(() => !disposed && setConnected(true))
      .catch(() => setConnected(false));

    return () => {
      disposed = true;
      void connection.stop();
      connRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [slug]);

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const open = useCallback(async (id: string) => {
    setSelected(id);
    setMessages(await chatApi.messages(id));
    const conn = connRef.current;
    if (conn?.state === HubConnectionState.Connected) await conn.invoke('JoinConversation', id);
  }, []);

  async function send() {
    const conn = connRef.current;
    if (!selected || !draft.trim() || conn?.state !== HubConnectionState.Connected) return;
    await conn.invoke('SendAgentMessage', selected, draft.trim());
    setDraft('');
  }

  // The bot answers until a human agent replies; an Agent message means we've taken over.
  const humanHandling = messages.some((m) => m.sender === 'Agent');

  if (!slug) {
    return <EmptyState icon={MessagesSquare} title={t('chat.title')} description={t('tenant.selectFirst')} />;
  }

  return (
    <div className="flex h-[calc(100vh-3.5rem)] flex-col">
      <div className="flex h-12 items-center justify-between border-b bg-card px-4">
        <span className="font-semibold">{t('chat.title')}</span>
        <Badge tone={connected ? 'success' : 'secondary'}>{connected ? '● live' : '○ offline'}</Badge>
      </div>
      <div className="flex flex-1 overflow-hidden">
        <aside className="w-72 shrink-0 overflow-y-auto border-r bg-card">
          {(conversations.data ?? []).map((c) => (
            <button
              key={c.id}
              type="button"
              onClick={() => void open(c.id)}
              className={cn(
                'flex w-full flex-col items-start gap-0.5 border-b px-4 py-3 text-left text-sm hover:bg-accent/40',
                selected === c.id && 'bg-accent',
              )}
            >
              <span className="font-medium">{c.visitorName || 'Visitor'}</span>
              <span className="text-xs text-muted-foreground">
                {c.status} · {new Date(c.lastMessageAt).toLocaleTimeString()}
              </span>
            </button>
          ))}
          {conversations.data?.length === 0 ? (
            <p className="px-4 py-6 text-sm text-muted-foreground">{t('common.noResults')}</p>
          ) : null}
        </aside>

        <div className="flex flex-1 flex-col bg-muted/20">
          {selected ? (
            <>
              <div className="flex items-center gap-2 border-b bg-card/60 px-4 py-2 text-xs text-muted-foreground">
                {humanHandling ? (
                  <>
                    <Badge tone="secondary">{t('chat.youHandling')}</Badge>
                    <span>{t('chat.youHandlingHint')}</span>
                  </>
                ) : (
                  <>
                    <Badge tone="default">
                      <Bot className="h-3 w-3" /> {t('chat.botHandling')}
                    </Badge>
                    <span>{t('chat.botHandlingHint')}</span>
                  </>
                )}
              </div>
              <div className="flex-1 space-y-2 overflow-y-auto p-4">
                {messages.map((m) => (
                  <div key={m.id} className={cn('flex flex-col', m.sender === 'Agent' && 'items-end')}>
                    {m.sender === 'Bot' ? (
                      <span className="mb-0.5 inline-flex items-center gap-1 text-[11px] font-medium text-muted-foreground">
                        <Bot className="h-3 w-3" /> {t('chat.assistant')}
                      </span>
                    ) : null}
                    <div
                      className={cn(
                        'max-w-[70%] rounded-2xl px-3.5 py-2 text-sm',
                        m.sender === 'Agent' && 'bg-primary text-primary-foreground',
                        m.sender === 'Bot' && 'bg-accent text-accent-foreground',
                        m.sender === 'Visitor' && 'bg-card text-card-foreground shadow-sm',
                      )}
                    >
                      {m.body}
                    </div>
                  </div>
                ))}
                <div ref={endRef} />
              </div>
              <div className="flex flex-col gap-1.5 border-t bg-card p-3">
                {!humanHandling ? (
                  <p className="text-xs text-muted-foreground">{t('chat.takeoverHint')}</p>
                ) : null}
                <div className="flex gap-2">
                  <Input
                    value={draft}
                    onChange={(e) => setDraft(e.target.value)}
                    onKeyDown={(e) => e.key === 'Enter' && void send()}
                    placeholder={t('chat.message')}
                  />
                  <Button onClick={() => void send()} disabled={!draft.trim()}>
                    <Send className="h-4 w-4" /> {t('chat.send')}
                  </Button>
                </div>
              </div>
            </>
          ) : (
            <div className="flex flex-1 items-center justify-center text-sm text-muted-foreground">
              {t('chat.noConversation')}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
