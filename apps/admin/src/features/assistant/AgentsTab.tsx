import { useNavigate } from '@tanstack/react-router';
import { ChevronRight, CircleAlert, CircleCheck, CirclePause, CircleSlash, ExternalLink, Loader2, Square } from 'lucide-react';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, cn, Spinner } from '@dcms/ui';
import { requestAgentFocus, type SiteSession, stopSite, useAgentHost } from '../agent/hostStore';

// The transcript and approval card bring the tool registry with them; only an opened row pays.
const AgentRunDetail = lazy(() => import('./AgentRunDetail'));

/**
 * Every site agent in this tab, from anywhere in the console (ADR 0033).
 *
 * <p>Site agents keep working after you leave their editor, and the assistant hands site changes
 * to them. This is where both are watched: which site, what it was asked, what it is doing, and
 * — opened — its transcript, its pending question and a way to stop it.</p>
 */
export function AgentsTab({ onLeave }: { onLeave: () => void }) {
  const { t } = useTranslation();
  const sessions = useAgentHost((state) => state.sessions);
  const [open, setOpen] = useState<string | null>(null);

  const list = Object.values(sessions)
    .filter((s) => s.running || s.queue.length > 0 || s.task !== null)
    // Working first, then most recent.
    .sort((a, b) => Number(b.running) - Number(a.running) || (b.startedAt ?? 0) - (a.startedAt ?? 0));

  if (list.length === 0) {
    return <p className="py-6 text-center text-sm text-muted-foreground">{t('assistant.agents.empty')}</p>;
  }

  return (
    <ul className="space-y-2">
      {list.map((session) => (
        <AgentRow
          key={session.key}
          session={session}
          expanded={open === session.key}
          onToggle={() => setOpen((was) => (was === session.key ? null : session.key))}
          onLeave={onLeave}
        />
      ))}
    </ul>
  );
}

function editorPath(key: string): string {
  return key.startsWith('repo:') ? `/repos/${key.slice('repo:'.length)}/code` : `/sites/${key}`;
}

function AgentRow({
  session,
  expanded,
  onToggle,
  onLeave,
}: {
  session: SiteSession;
  expanded: boolean;
  onToggle: () => void;
  onLeave: () => void;
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();

  return (
    <li className={cn('rounded-md border', session.pending && 'border-warning/60')}>
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={expanded}
        className="flex w-full items-start gap-2 px-3 py-2 text-left hover:bg-accent/50"
      >
        <ChevronRight
          className={cn('mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform', expanded && 'rotate-90')}
          aria-hidden
        />
        <span className="min-w-0 flex-1">
          <span className="flex min-w-0 items-center gap-1.5">
            <span className="truncate text-sm font-medium">{session.siteName ?? session.key}</span>
            <Badge tone="default" className="shrink-0">
              {t(`assistant.agents.kind.${session.kind}`)}
            </Badge>
            {session.origin === 'delegated' ? (
              <Badge tone="secondary" className="shrink-0">
                {t('assistant.agents.delegated')}
              </Badge>
            ) : null}
          </span>
          {session.task ? (
            <span className="mt-0.5 line-clamp-2 block break-words text-xs text-muted-foreground">{session.task}</span>
          ) : null}
          <Status session={session} />
        </span>
      </button>

      {expanded ? (
        <div className="border-t">
          <div className="flex flex-wrap items-center gap-2 px-3 py-2">
            {session.running ? (
              <Button size="sm" variant="outline" onClick={() => stopSite(session.key)}>
                <Square className="h-3.5 w-3.5" aria-hidden />
                {t('common.stop')}
              </Button>
            ) : null}
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                requestAgentFocus(session.key);
                onLeave();
                void navigate({ to: editorPath(session.key) as string });
              }}
            >
              <ExternalLink className="h-3.5 w-3.5" aria-hidden />
              {t('assistant.agents.openEditor')}
            </Button>
          </div>
          <Suspense fallback={<Spinner className="m-3" />}>
            <AgentRunDetail siteKey={session.key} />
          </Suspense>
        </div>
      ) : null}
    </li>
  );
}

function Status({ session }: { session: SiteSession }) {
  const { t } = useTranslation();
  const queued = session.queue.length > 0 ? ` · ${t('assistant.agents.queued', { count: session.queue.length })}` : '';

  if (session.pending) {
    return (
      <span className="mt-1 flex items-center gap-1 text-2xs font-medium text-warning-foreground">
        <CircleAlert className="h-3 w-3" aria-hidden />
        {t('assistant.agents.waiting')}
        {queued}
      </span>
    );
  }
  if (session.running) {
    return (
      <span className="mt-1 flex items-center gap-1 text-2xs text-muted-foreground">
        <Loader2 className="h-3 w-3 animate-spin motion-reduce:animate-none" aria-hidden />
        <span className="truncate">
          {session.step ?? t('assistant.agents.working')} · {t('assistant.agents.turn', { count: session.turns })}
          {queued}
        </span>
      </span>
    );
  }

  const icon =
    session.outcome === 'completed' ? (
      <CircleCheck className="h-3 w-3 text-success" aria-hidden />
    ) : session.outcome === 'paused' ? (
      <CirclePause className="h-3 w-3 text-warning-foreground" aria-hidden />
    ) : session.outcome === 'failed' ? (
      <CircleAlert className="h-3 w-3 text-destructive" aria-hidden />
    ) : (
      <CircleSlash className="h-3 w-3" aria-hidden />
    );
  return (
    <span className="mt-1 flex items-center gap-1 text-2xs text-muted-foreground">
      {icon}
      {t(`assistant.agents.outcome.${session.outcome ?? 'stopped'}`)}
      {session.changes.length > 0 ? ` · ${t('assistant.agents.files', { count: session.changes.length })}` : ''}
      {queued}
    </span>
  );
}
