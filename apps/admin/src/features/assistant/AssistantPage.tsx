import { Check, KeyRound, Link2, PanelRightClose, Users } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from '@tanstack/react-router';
import { toast } from 'sonner';
import { Button, Spinner, cn } from '@dcms/ui';
import { useAi } from './context';
import { ApprovalCard } from './ApprovalCard';
import { Composer } from './Composer';
import { ConversationRail } from './ConversationRail';
import { type ConversationScope, useConversation, useUpdateConversation } from './conversations';
import { Transcript } from './Transcript';
import type { ToolStep } from './steps';
import { useAssistantSession } from './useAssistantSession';
import { WorkDetail } from './WorkCard';

/**
 * The assistant, at full width.
 *
 * <p>The dock is for a question about the page you are on. This is for the work: a run of
 * fifteen steps with a field diff in the middle of it does not fit in a 26rem sheet, and
 * neither does the history that makes any of it worth keeping. Three panes — what you have
 * done, what is happening, and the inside of whichever step you clicked.</p>
 */
export function AssistantPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { conversationId: routeId } = useParams({ strict: false }) as { conversationId?: string };
  const { page } = useAi();

  const [scope, setScope] = useState<ConversationScope>('mine');
  const [selected, setSelected] = useState<ToolStep | null>(null);
  const [elapsed, setElapsed] = useState(0);
  const endRef = useRef<HTMLDivElement>(null);

  const conversationId = routeId ?? null;
  const session = useAssistantSession(page, {
    conversationId,
    onConversationChange: (id) =>
      void navigate({
        to: '/assistant/$conversationId' as string,
        params: { conversationId: id } as never,
        replace: true,
      }),
  });

  const detail = useConversation(conversationId);
  const update = useUpdateConversation();

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

  const shared = detail.data?.visibility === 'Workspace';

  return (
    <div className="flex h-[calc(100dvh-var(--dcms-topbar-h,3.5rem))] min-h-0">
      <aside className="hidden w-60 shrink-0 border-r md:block">
        <ConversationRail
          scope={scope}
          onScopeChange={setScope}
          selectedId={conversationId}
          onSelect={(id) =>
            void navigate({
              to: '/assistant/$conversationId' as string,
              params: { conversationId: id } as never,
            })
          }
          onNew={() => {
            session.reset();
            void navigate({ to: '/assistant' as string });
          }}
        />
      </aside>

      <main className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-12 shrink-0 items-center gap-2 border-b px-4">
          <h1 className="min-w-0 flex-1 truncate text-sm font-medium">
            {detail.data?.title ?? t('assistant.newChat')}
          </h1>

          {conversationId && detail.data?.mine ? (
            <>
              <Button
                variant="ghost"
                size="sm"
                className="h-7 gap-1.5 px-2"
                onClick={() =>
                  void update.mutateAsync({
                    id: conversationId,
                    visibility: shared ? 'Private' : 'Workspace',
                  })
                }
              >
                {shared ? (
                  <Check className="h-3.5 w-3.5" aria-hidden />
                ) : (
                  <Users className="h-3.5 w-3.5" aria-hidden />
                )}
                <span className="text-xs">
                  {shared ? t('assistant.sharedWithWorkspace') : t('assistant.share')}
                </span>
              </Button>
              {/* Only offered once it is shared: handing someone a link they cannot open is
                  worse than not offering one. */}
              {shared ? (
                <Button
                  variant="ghost"
                  size="icon"
                  className="h-7 w-7"
                  aria-label={t('assistant.copyLink')}
                  title={t('assistant.copyLink')}
                  onClick={() => {
                    void navigator.clipboard.writeText(window.location.href);
                    toast.success(t('assistant.linkCopied'));
                  }}
                >
                  <Link2 className="h-3.5 w-3.5" aria-hidden />
                </Button>
              ) : null}
            </>
          ) : null}
        </header>

        <div className="min-h-0 flex-1 overflow-y-auto px-4 py-4 text-sm">
          {session.loading ? (
            <Spinner />
          ) : session.steps.length === 0 ? (
            <Empty onPick={session.send} />
          ) : (
            <Transcript
              steps={session.steps}
              running={session.running}
              elapsed={elapsed}
              onStop={session.stop}
              selectedStepId={selected?.id ?? null}
              onSelectStep={setSelected}
            />
          )}

          {session.pending ? (
            <div className="mt-3">
              <ApprovalCard pending={session.pending} onDecide={session.approve} />
            </div>
          ) : null}

          {session.needsKey ? <NoKey provider={session.keyProvider} /> : null}
          <div ref={endRef} />
        </div>

        <div className="shrink-0 border-t p-3">
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
            />
          )}
        </div>
      </main>

      {/* The inspector. Below xl it is not there at all and the card opens in place instead —
          a 20rem pane on a 13" laptop takes the width the transcript needs to be readable. */}
      <aside
        className={cn(
          'hidden w-80 shrink-0 overflow-y-auto border-l p-3 xl:block',
          !selected && 'xl:hidden',
        )}
      >
        {selected ? (
          <>
            <div className="mb-2 flex items-center gap-2">
              <h2 className="flex-1 truncate text-sm font-medium">{selected.label}</h2>
              <Button
                variant="ghost"
                size="icon"
                className="h-7 w-7"
                onClick={() => setSelected(null)}
                aria-label={t('actions.close')}
              >
                <PanelRightClose className="h-3.5 w-3.5" aria-hidden />
              </Button>
            </div>
            <WorkDetail step={selected} />
          </>
        ) : null}
      </aside>
    </div>
  );
}

function Empty({ onPick }: { onPick: (prompt: string) => void }) {
  const { t } = useTranslation();
  const suggestions = [
    t('assistant.suggestTraffic'),
    t('assistant.suggestContent'),
    t('assistant.suggestDraft'),
  ];
  return (
    <div className="mx-auto max-w-[60ch] space-y-4 py-10">
      <p className="text-base">{t('assistant.pageIntro')}</p>
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
    </div>
  );
}

function NoKey({ provider }: { provider: string | null }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  return (
    <div className="mt-3 rounded-md border border-[hsl(var(--warning)/0.4)] bg-[hsl(var(--warning)/0.1)] p-3 text-sm">
      <p className="flex items-center gap-1.5 font-medium">
        <KeyRound className="h-4 w-4" aria-hidden />
        {t('assistant.noKeyTitle')}
      </p>
      <p className="mt-1 text-muted-foreground">
        {provider ? t('assistant.noKeyFor', { provider }) : t('assistant.noKeyBody')}
      </p>
      <Button
        variant="outline"
        size="sm"
        className="mt-2"
        onClick={() => void navigate({ to: '/settings/ai' as string })}
      >
        {t('assistant.connectAccount')}
      </Button>
    </div>
  );
}
