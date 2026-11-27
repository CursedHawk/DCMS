import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Bot, MessagesSquare, Send } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { Badge, Button, cn, EmptyState, Input } from '@dcms/ui';
import { usePluginHost, usePluginT } from '@dcms/plugin-ui';

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

/**
 * The plugin's console hint (`plugin:live-chat:conversations`, ADR 0037 / #669): every new
 * conversation and message makes the console's notification hub invalidate this prefix, so the
 * list and the open conversation refetch. No socket of this page's own.
 */
const KEY = 'plugin:live-chat:conversations';

/**
 * The live chat console: every conversation as it happens, with takeover from the chatbot.
 * A screen of the AI Chatbot plugin (manifest screen "console"), at /apps/{slug}/console.
 * Reads and writes are the plugin's admin routes; the visitor hears a reply on the app's
 * WebSockets channel.
 */
export function ConsoleScreen() {
  const { t } = usePluginT();
  const host = usePluginHost();
  const qc = useQueryClient();
  const slug = host.tenant;

  const [selected, setSelected] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);

  const conversations = useQuery({
    queryKey: [KEY],
    queryFn: () => host.api.get<ChatConversation[]>('/admin/chat/conversations'),
    enabled: !!slug,
  });
  const thread = useQuery({
    queryKey: [KEY, selected, 'messages'],
    queryFn: () => host.api.get<ChatMessage[]>(`/admin/chat/conversations/${selected}/messages`),
    enabled: !!selected,
  });
  const messages = thread.data ?? [];

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages.length]);

  async function send() {
    if (!selected || !draft.trim() || sending) return;
    setSending(true);
    try {
      await host.api.post(`/admin/chat/conversations/${selected}/messages`, { body: draft.trim() });
      setDraft('');
      await qc.invalidateQueries({ queryKey: [KEY] });
    } finally {
      setSending(false);
    }
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
      </div>
      <div className="flex flex-1 overflow-hidden">
        <aside className="w-72 shrink-0 overflow-y-auto border-r bg-card">
          {(conversations.data ?? []).map((c) => (
            <button
              key={c.id}
              type="button"
              onClick={() => setSelected(c.id)}
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
                      <span className="mb-0.5 inline-flex items-center gap-1 text-2xs font-medium text-muted-foreground">
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
                  <Button onClick={() => void send()} disabled={!draft.trim() || sending}>
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
