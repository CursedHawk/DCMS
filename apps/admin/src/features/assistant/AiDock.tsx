import { Bot, History, KeyRound, Maximize2, MessageSquarePlus } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import {
  Badge,
  Button,
  Sheet,
  SheetBody,
  SheetContent,
  SheetFooter,
  SheetHeader,
  SheetTitle,
  Callout,
  Tabs,
  TabsList,
  TabsTrigger,
} from '@dcms/ui';
import { useAi } from './context';
import { ApprovalCard } from './ApprovalCard';
import { Composer } from './Composer';
import { ConversationRail } from './ConversationRail';
import { type ConversationScope } from './conversations';
import { Transcript } from './Transcript';
import { AgentsTab } from './AgentsTab';
import { useSharedAssistant } from './session';
import { useAgentActivity } from '../agent/hostStore';

/**
 * The assistant, on every page.
 *
 * <p>A side panel rather than a route, because the point is that it sits <em>beside</em> what
 * you are doing. Sending someone to /assistant to ask about the page they just left is the
 * shape that makes an assistant feel bolted on — so both exist, and the dock hands its
 * conversation to the full view rather than restarting it there.</p>
 *
 * <p>It knows where the reader is (`useAiPageContext`) and it is given only the tools that
 * reader's permissions allow — so it can never offer to do something the API would refuse.</p>
 */
export function AiDock() {
  const { t } = useTranslation();
  const { open, setOpen, page, prefill, takePrefill } = useAi();
  const navigate = useNavigate();
  const [showHistory, setShowHistory] = useState(false);
  const [scope, setScope] = useState<ConversationScope>('mine');
  const [selectedStepId, setSelectedStepId] = useState<string | null>(null);
  const [elapsed, setElapsed] = useState(0);
  const endRef = useRef<HTMLDivElement>(null);

  // The tab's one assistant session, shared with the full page (ADR 0033).
  const session = useSharedAssistant();
  const { conversationId } = session;
  const [tab, setTab] = useState<'chat' | 'agents'>('chat');
  const agents = useAgentActivity();

  useEffect(() => {
    endRef.current?.scrollIntoView({ block: 'end' });
  }, [session.steps, session.pending]);

  useEffect(() => {
    if (!session.running) {
      setElapsed(0);
      return;
    }
    const started = Date.now();
    const timer = window.setInterval(() => setElapsed((Date.now() - started) / 1000), 1000);
    return () => window.clearInterval(timer);
  }, [session.running]);

  return (
    <Sheet open={open} onOpenChange={setOpen}>
      <SheetContent side="right" width="w-[30rem]" className="p-0">
        <SheetHeader className="flex-row items-center gap-1">
          <Bot className="h-4 w-4 shrink-0 text-primary" aria-hidden />
          <SheetTitle className="flex-1">{t('assistant.title')}</SheetTitle>

          <Button
            variant="ghost"
            size="icon"
            className="h-7 w-7"
            aria-label={t('assistant.history')}
            title={t('assistant.history')}
            onClick={() => setShowHistory((was) => !was)}
          >
            <History className="h-3.5 w-3.5" aria-hidden />
          </Button>
          <Button
            variant="ghost"
            size="icon"
            className="h-7 w-7"
            aria-label={t('assistant.newChat')}
            title={t('assistant.newChat')}
            onClick={() => {
              session.reset();
              session.select(null);
              setShowHistory(false);
              setTab('chat');
            }}
          >
            <MessageSquarePlus className="h-3.5 w-3.5" aria-hidden />
          </Button>
          {/* Carries the conversation across rather than starting a second one: the run you
              want more room for is the one already on screen. */}
          <Button
            variant="ghost"
            size="icon"
            className="h-7 w-7"
            aria-label={t('assistant.openFull')}
            title={t('assistant.openFull')}
            onClick={() => {
              setOpen(false);
              void navigate(
                conversationId
                  ? {
                      to: '/assistant/$conversationId' as string,
                      params: { conversationId } as never,
                    }
                  : { to: '/assistant' as string },
              );
            }}
          >
            <Maximize2 className="h-3.5 w-3.5" aria-hidden />
          </Button>
        </SheetHeader>

        {/* The assistant's own chat, and every site agent in this tab — including the ones it
            asked for. */}
        {showHistory ? null : (
          <Tabs value={tab} onValueChange={(v) => setTab(v as 'chat' | 'agents')} className="px-4 pt-2">
            <TabsList>
              <TabsTrigger value="chat">{t('assistant.agents.chatTab')}</TabsTrigger>
              <TabsTrigger value="agents">
                {t('assistant.agents.tab')}
                {agents.working > 0 ? (
                  <Badge tone={agents.waiting ? 'warning' : 'default'} className="ml-1.5">
                    {agents.working}
                  </Badge>
                ) : null}
              </TabsTrigger>
            </TabsList>
          </Tabs>
        )}

        {showHistory ? (
          <div className="min-h-0 flex-1">
            <ConversationRail
              scope={scope}
              onScopeChange={setScope}
              selectedId={conversationId}
              onSelect={(id) => {
                session.select(id);
                setShowHistory(false);
              }}
              onNew={() => {
                session.reset();
                session.select(null);
                setShowHistory(false);
              }}
            />
          </div>
        ) : tab === 'agents' ? (
          <SheetBody>
            <AgentsTab onLeave={() => setOpen(false)} />
          </SheetBody>
        ) : (
          <SheetBody className="space-y-3">
            {session.steps.length === 0 ? (
              <Empty
                page={page?.summary}
                toolCount={session.toolCount}
                onPick={(prompt) => session.send(prompt)}
              />
            ) : (
              <Transcript
                steps={session.steps}
                running={session.running}
                elapsed={elapsed}
                onStop={session.stop}
                selectedStepId={selectedStepId}
                onSelectStep={(step) => setSelectedStepId(step?.id ?? null)}
                compact
              />
            )}

            {session.pending ? (
              <ApprovalCard pending={session.pending} onDecide={session.approve} />
            ) : null}

            {session.needsKey ? (
              <Callout tone="warning">
                <p className="flex items-center gap-1.5 font-medium">
                  <KeyRound className="h-4 w-4" aria-hidden />
                  {t('assistant.noKeyTitle')}
                </p>
                {/* Names the provider the server resolved, so nobody is sent to create an
                    account for a service their workspace does not use. */}
                <p className="mt-1 text-muted-foreground">
                  {session.keyProvider
                    ? t('assistant.noKeyFor', { provider: session.keyProvider })
                    : t('assistant.noKeyBody')}
                </p>
                <Button
                  variant="outline"
                  size="sm"
                  className="mt-2"
                  onClick={() => {
                    setOpen(false);
                    void navigate({ to: '/settings/ai' as string });
                  }}
                >
                  {t('assistant.connectAccount')}
                </Button>
              </Callout>
            ) : null}

            <div ref={endRef} />
          </SheetBody>
        )}

        {showHistory || tab === 'agents' ? null : (
          <SheetFooter className="flex-col items-stretch gap-2">
            {session.readOnly ? (
              <p className="text-xs text-muted-foreground">{t('assistant.readOnlyChat')}</p>
            ) : (
              <Composer
                onSend={session.send}
                onStop={session.stop}
                running={session.running}
                mode={session.mode}
                onModeChange={session.setMode}
                writable={session.writable}
                attachments={session.attachments}
                onAttach={session.attach}
                onDetach={session.detach}
                toolCount={session.toolCount}
                unsaved={session.unsaved}
                prefill={prefill}
                onPrefillTaken={takePrefill}
              />
            )}
          </SheetFooter>
        )}
      </SheetContent>
    </Sheet>
  );
}

function Empty({
  page,
  toolCount,
  onPick,
}: {
  page?: string;
  toolCount: number;
  onPick: (prompt: string) => void;
}) {
  const { t } = useTranslation();
  // Suggestions rather than a blank box: nobody's first thought is what to ask a new
  // assistant, and an empty panel is where most of them are abandoned.
  const suggestions = [
    t('assistant.suggestTraffic'),
    t('assistant.suggestContent'),
    t('assistant.suggestPlugins'),
  ];

  return (
    <div className="space-y-3 py-4 text-sm">
      <p className="text-muted-foreground">
        {page ? t('assistant.hereContext', { page }) : t('assistant.hereGeneric')}
      </p>
      {toolCount === 0 ? (
        <Badge tone="warning">{t('assistant.noTools')}</Badge>
      ) : (
        <ul className="space-y-1.5">
          {suggestions.map((prompt) => (
            <li key={prompt}>
              <button
                type="button"
                onClick={() => onPick(prompt)}
                className="w-full rounded-md border px-3 py-2 text-left text-sm text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
              >
                {prompt}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
