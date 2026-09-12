import { Link } from '@tanstack/react-router';
import { CloudOff, History, MessageSquarePlus, RotateCcw, Send, Sparkles, Square } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, cn } from '@dcms/ui';
import { AI_MODES, type AiMode } from '../../agent/modes';
import { ConversationRail } from '../../assistant/ConversationRail';
import type { ConversationScope } from '../../assistant/conversations';
import { ApprovalCard } from './ApprovalCard';
import { ChangeReview } from './ChangeReview';
import type { AgentRunMetrics } from './runMetrics';
import { Transcript } from './Transcript';
import { useAgentSession } from './useAgentSession';

// The router's typed route union is intentionally loose here (see routes.tsx); a
// string-typed path matches the existing pattern (e.g. sitesPath in IdePage).
const aiSettingsPath: string = '/settings/ai';

/** The provider's own name, from the enum value the server sends. */
/**
 * What the last run cost.
 *
 * <p><b>An unknown is shown as unknown.</b> Not every provider reports the prompt side of a
 * turn, and a meter that renders a missing number as zero would report a saving that never
 * happened — which is the exact failure the whole measurement effort exists to avoid.</p>
 *
 * <p>No cost in currency. Price depends on the model, the tier and the workspace's own
 * contract, none of which the browser knows; a number invented from a hardcoded rate card would
 * be worse than no number, because people would believe it.</p>
 */
function RunMeter({ run }: { run: AgentRunMetrics }) {
  const { t } = useTranslation();

  return (
    <dl className="flex shrink-0 items-baseline gap-3 border-t px-3 py-1 font-mono text-[10px] text-muted-foreground">
      <Stat label={t('ide.agent.meter.turns')} value={String(run.turns)} />
      <Stat label={t('ide.agent.meter.tools')} value={String(run.toolCalls)} />
      <Stat
        label={t('ide.agent.meter.in')}
        value={run.inputTokens === null ? '—' : compact(run.inputTokens)}
        title={run.inputTokens === null ? t('ide.agent.meter.inUnknown') : undefined}
      />
      <Stat label={t('ide.agent.meter.out')} value={compact(run.outputTokens)} />
      {run.cacheReadTokens > 0 ? (
        <Stat label={t('ide.agent.meter.cached')} value={compact(run.cacheReadTokens)} />
      ) : null}
      <span className="flex-1" />
      <span className="tabular-nums">{(run.wallMs / 1000).toFixed(1)}s</span>
    </dl>
  );
}

function Stat({ label, value, title }: { label: string; value: string; title?: string }) {
  return (
    <span className="flex items-baseline gap-1" title={title}>
      <dt className="text-muted-foreground/60">{label}</dt>
      <dd className="tabular-nums text-foreground">{value}</dd>
    </span>
  );
}

/** 12400 → 12.4k. Token counts are read for magnitude, not for their last three digits. */
function compact(n: number): string {
  if (n < 1000) return String(n);
  return `${(n / 1000).toFixed(1)}k`;
}

function providerLabel(provider: string): string {
  const names: Record<string, string> = {
    Anthropic: 'Anthropic',
    OpenAi: 'OpenAI',
    Ollama: 'Ollama',
    LmStudio: 'LM Studio',
  };
  return names[provider] ?? provider;
}

// The IDE agent chat panel. Claude runs its tool loop here in the browser, editing
// the live VFS; the user watches files change in their tabs and the preview refresh.
/** Short labels for the shared four-mode model (see `features/agent/modes.ts`). */
const MODE_LABEL: Record<AiMode, string> = {
  read: 'Read',
  careful: 'Careful',
  agent: 'Agent',
  auto: 'Full auto',
};

export function AgentPanel({ siteId, siteName }: { siteId: string; siteName?: string }) {
  const { t } = useTranslation();
  const session = useAgentSession({ siteId, siteName });
  const [input, setInput] = useState('');
  const [historyOpen, setHistoryOpen] = useState(false);
  const [scope, setScope] = useState<ConversationScope>('mine');
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight });
  }, [session.entries, session.pending]);

  const submit = () => {
    if (!input.trim() || session.running) return;
    session.send(input);
    setInput('');
  };

  return (
    <div className="flex h-full flex-col">
      {/* Header */}
      <div className="flex h-9 shrink-0 items-center gap-2 border-b px-3 text-sm font-medium">
        <Sparkles className="h-4 w-4 text-primary" />
        <span className="flex-1">{t('ide.agent.title')}</span>
        <button
          type="button"
          onClick={() => setHistoryOpen((open) => !open)}
          title={t('ide.agent.history', 'History')}
          aria-pressed={historyOpen}
          className={cn(
            'hover:text-foreground',
            historyOpen ? 'text-foreground' : 'text-muted-foreground',
          )}
        >
          <History className="h-3.5 w-3.5" />
        </button>
        <button
          type="button"
          onClick={session.newConversation}
          title={t('ide.agent.newConversation', 'New conversation')}
          className="text-muted-foreground hover:text-foreground"
        >
          <MessageSquarePlus className="h-3.5 w-3.5" />
        </button>
        <button
          type="button"
          onClick={session.reset}
          title={t('common.reset', 'Reset')}
          className="text-muted-foreground hover:text-foreground"
        >
          <RotateCcw className="h-3.5 w-3.5" />
        </button>
      </div>

      {/* History. Scoped to this site's IDE conversations: the console's chats are a different
          surface and listing them here would be a different product in the same panel. */}
      {historyOpen && (
        <div className="h-56 shrink-0 overflow-hidden border-b">
          <ConversationRail
            surface="ide"
            siteId={siteId}
            scope={scope}
            onScopeChange={setScope}
            selectedId={session.conversationId}
            onSelect={(id) => {
              void session.resume(id);
              setHistoryOpen(false);
            }}
            onNew={() => {
              session.newConversation();
              setHistoryOpen(false);
            }}
          />
        </div>
      )}

      {/* Transcript */}
      <div ref={scrollRef} className="min-h-0 flex-1 overflow-y-auto">
        {session.entries.length === 0 && (
          <p className="px-4 py-6 text-center text-xs text-muted-foreground">
            {t('ide.agent.empty')}
          </p>
        )}
        <Transcript entries={session.entries} />

        {/* The approval gate. Inside the scroll container and at the end of it, so it arrives
            where the run left off rather than as chrome pinned somewhere else on screen. */}
        {session.pending && (
          <ApprovalCard calls={session.pending.tools} onDecide={session.approve} />
        )}
      </div>

      {/* What the run changed, and how to undo it. Below the transcript rather than inside it:
          the transcript is a history and this is the current state of the workspace. */}
      <ChangeReview
        changes={session.changes}
        onRevert={session.revert}
        onRevertAll={session.revertAll}
      />

      {session.lastRun ? <RunMeter run={session.lastRun} /> : null}

      {/* Said out loud rather than retried silently. The transcript is the record of an agent
          writing to the workspace, so "this run is not being recorded" is something the operator
          should know while it is still running — not discover afterwards from a gap. */}
      {session.unsaved && (
        <div className="mx-3 mb-2 flex items-start gap-1.5 rounded-md border border-amber-500/40 bg-amber-500/10 p-2 text-[11px] text-amber-700 dark:text-amber-400">
          <CloudOff className="mt-px h-3 w-3 shrink-0" aria-hidden />
          <span>{t('ide.agent.unsaved')}</span>
        </div>
      )}

      {/* Needs-key banner */}
      {session.needsKey && (
        <div className="mx-3 mb-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-2 text-xs">
          <p className="mb-1 font-medium">{t('ide.agent.needsKeyTitle')}</p>
          {/* Names the provider the server actually resolved. Telling somebody whose workspace
              runs on OpenAI to connect an Anthropic account sends them to make an account they
              do not need. */}
          <p className="text-muted-foreground">
            {session.keyProvider
              ? t('ide.agent.needsKeyFor', { provider: providerLabel(session.keyProvider) })
              : t('ide.agent.needsKeyBody')}
          </p>
          <Link
            to={aiSettingsPath}
            className="mt-1 inline-block font-medium text-primary hover:underline"
          >
            {t('ide.agent.needsKeyLink', 'Open AI settings →')}
          </Link>
        </div>
      )}

      {/* Composer */}
      <div className="shrink-0 border-t p-2">
        <div className="flex items-end gap-2">
          <textarea
            value={input}
            onChange={(ev) => setInput(ev.target.value)}
            onKeyDown={(ev) => {
              if (ev.key === 'Enter' && !ev.shiftKey) {
                ev.preventDefault();
                submit();
              }
            }}
            rows={2}
            placeholder={t('ide.agent.placeholder', 'Describe a change…')}
            className="min-h-9 flex-1 resize-none rounded-md border bg-background px-2 py-1.5 text-sm outline-none focus:ring-1 focus:ring-primary"
          />
          {session.running ? (
            <Button
              size="icon"
              variant="outline"
              onClick={session.stop}
              title={t('common.stop', 'Stop')}
            >
              <Square className="h-4 w-4" />
            </Button>
          ) : (
            <Button
              size="icon"
              onClick={submit}
              disabled={!input.trim()}
              title={t('common.send', 'Send')}
            >
              <Send className="h-4 w-4" />
            </Button>
          )}
        </div>

        {/* How much the agent may do without asking. Here rather than in the header because it
            is a property of the next thing you send — the same place every other agent surface
            puts its model picker — and because the header already has three actions competing
            for a 240px column.

            `read` and `careful` are deliberately reachable: reviewing what the agent WOULD do
            is a legitimate way to use it on a site that is already live. */}
        <select
          value={session.mode}
          onChange={(ev) => session.setMode(ev.target.value as AiMode)}
          title={t('ide.agent.modeHint')}
          aria-label={t('ide.agent.mode')}
          className="mt-1.5 rounded border-0 bg-transparent px-1 py-0.5 text-[11px] font-medium text-muted-foreground outline-none hover:bg-accent focus:ring-1 focus:ring-primary"
        >
          {AI_MODES.map((m) => (
            <option key={m} value={m}>
              {MODE_LABEL[m]}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}
