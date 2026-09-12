import {
  Check,
  ChevronRight,
  CircleAlert,
  CircleCheck,
  CircleSlash,
  Loader2,
  RotateCw,
  X,
} from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { useVfs } from '../../site-source/vfs';
import type { AgentEntry } from './useAgentSession';

/**
 * What the agent is doing, as it does it.
 *
 * <p>Every line here used to be grey text. A tool call read `edit_file` and nothing more — not
 * which file, not what it changed, not whether it worked. Watching an agent work is the whole
 * reason this panel is open, so the run has to be legible: each call is a card you can open to
 * see the arguments it was given and the answer it got back.</p>
 *
 * <p><b>Collapsed by default, and that is the point.</b> A run is twenty calls long; expanding
 * all of them turns the panel into a log nobody reads. The card's closed state carries the one
 * line that matters — what it did and to what — and the detail waits behind a click.</p>
 */
export function Transcript({ entries }: { entries: readonly AgentEntry[] }) {
  return (
    <div className="space-y-2 p-3 text-sm">
      {entries.map((entry) => (
        <Entry key={entry.id} entry={entry} />
      ))}
    </div>
  );
}

function Entry({ entry }: { entry: AgentEntry }) {
  switch (entry.kind) {
    case 'user':
      return (
        <p className="ml-6 whitespace-pre-wrap rounded-md bg-primary/10 px-2.5 py-1.5 text-foreground">
          {entry.text}
        </p>
      );

    case 'assistant':
      return entry.text ? (
        <p className="whitespace-pre-wrap px-0.5 text-foreground">{entry.text}</p>
      ) : null;

    case 'thinking':
      return <Thinking text={entry.text} />;

    case 'tool':
      return <ToolCard entry={entry} />;

    case 'check':
      return <CheckLine status={entry.status} />;

    case 'retry':
      return (
        <p className="flex items-start gap-1.5 px-1 text-[11px] text-[hsl(var(--warning))]">
          <RotateCw className="mt-px h-3 w-3 shrink-0" aria-hidden />
          <span>
            <RetryText attempt={entry.attempt} of={entry.of} because={entry.because} />
          </span>
        </p>
      );

    case 'error':
      return (
        <p className="flex items-start gap-1.5 rounded-md bg-destructive/10 px-2 py-1.5 text-xs text-destructive">
          <CircleAlert className="mt-px h-3.5 w-3.5 shrink-0" aria-hidden />
          <span className="min-w-0 whitespace-pre-wrap">{entry.text}</span>
        </p>
      );
  }
}

function RetryText({ attempt, of, because }: { attempt: number; of: number; because: string }) {
  const { t } = useTranslation();
  return <>{t('ide.agent.retrying', { attempt, of, because })}</>;
}

/**
 * The model's reasoning.
 *
 * <p>Collapsed, and quieter than the answer. It is genuinely useful when a run goes somewhere
 * unexpected and noise the rest of the time, which is exactly the shape of something that
 * should be one click away rather than on screen.</p>
 */
function Thinking({ text }: { text: string }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  if (!text.trim()) return null;

  return (
    <div className="px-0.5">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="flex items-center gap-1 text-[11px] text-muted-foreground hover:text-foreground"
      >
        <ChevronRight
          className={cn('h-3 w-3 transition-transform', open && 'rotate-90')}
          aria-hidden
        />
        {t('ide.agent.thinking')}
      </button>
      {open ? (
        <p className="mt-1 whitespace-pre-wrap border-l pl-2 text-[11px] leading-relaxed text-muted-foreground">
          {text}
        </p>
      ) : null}
    </div>
  );
}

function ToolCard({ entry }: { entry: Extract<AgentEntry, { kind: 'tool' }> }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const open_ = useVfs((s) => s.open);

  const duration =
    entry.endedAt && entry.startedAt ? Math.max(0, entry.endedAt - entry.startedAt) : null;

  return (
    <div
      className={cn(
        'rounded-md border text-[11px]',
        entry.status === 'error' ? 'border-destructive/40 bg-destructive/5' : 'border-border',
      )}
    >
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="flex w-full items-center gap-1.5 px-2 py-1.5 text-left hover:bg-accent/50"
      >
        <ChevronRight
          className={cn('h-3 w-3 shrink-0 text-muted-foreground transition-transform', open && 'rotate-90')}
          aria-hidden
        />
        <ToolStatus status={entry.status} />
        <span className="shrink-0 font-mono text-foreground">{entry.name}</span>
        <span className="min-w-0 flex-1 truncate text-muted-foreground">{entry.label}</span>
        {duration != null ? (
          <span className="shrink-0 tabular-nums text-muted-foreground/60">
            {formatDuration(duration)}
          </span>
        ) : null}
      </button>

      {open ? (
        <div className="space-y-2 border-t px-2 py-1.5">
          <Labelled label={t('ide.agent.arguments')}>
            <pre className="overflow-x-auto whitespace-pre-wrap break-words font-mono text-[10px] leading-relaxed text-muted-foreground">
              {JSON.stringify(entry.input, null, 2)}
            </pre>
          </Labelled>
          {entry.result ? (
            <Labelled label={t('ide.agent.result')}>
              <pre
                className={cn(
                  'max-h-48 overflow-auto whitespace-pre-wrap break-words font-mono text-[10px] leading-relaxed',
                  entry.status === 'error' ? 'text-destructive' : 'text-muted-foreground',
                )}
              >
                {entry.result}
              </pre>
            </Labelled>
          ) : null}
        </div>
      ) : null}

      {/* Files the call wrote, always visible — this is the part of a run somebody needs to
          check, and burying it behind the same click as the raw arguments would hide it. */}
      {entry.paths && entry.paths.length > 0 ? (
        <ul className="flex flex-wrap gap-1 border-t px-2 py-1">
          {entry.paths.map((path) => (
            <li key={path}>
              <button
                type="button"
                onClick={() => open_(path)}
                className="rounded bg-primary/10 px-1.5 py-px font-mono text-[10px] text-primary hover:bg-primary/20"
              >
                {path}
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

function Labelled({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <p className="mb-0.5 text-[10px] font-medium text-muted-foreground">{label}</p>
      {children}
    </div>
  );
}

function ToolStatus({ status }: { status: 'running' | 'ok' | 'error' | 'declined' }) {
  if (status === 'running') {
    return <Loader2 className="h-3 w-3 shrink-0 animate-spin text-muted-foreground" aria-hidden />;
  }
  if (status === 'error') return <X className="h-3 w-3 shrink-0 text-destructive" aria-hidden />;
  if (status === 'declined') {
    return <CircleSlash className="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden />;
  }
  return <Check className="h-3 w-3 shrink-0 text-[hsl(var(--success))]" aria-hidden />;
}

/**
 * The deterministic gate's verdict.
 *
 * <p>`skipped` is its own state and not a quiet success: the preview was closed, so nothing was
 * built and nothing was checked. Showing that as a tick would be the panel claiming a guarantee
 * the run never had.</p>
 */
function CheckLine({ status }: { status: 'running' | 'passed' | 'failed' | 'skipped' }) {
  const { t } = useTranslation();

  const map = {
    running: {
      icon: <Loader2 className="h-3 w-3 animate-spin" aria-hidden />,
      text: t('ide.agent.check.running'),
      tone: 'text-muted-foreground',
    },
    passed: {
      icon: <CircleCheck className="h-3 w-3" aria-hidden />,
      text: t('ide.agent.check.passed'),
      tone: 'text-[hsl(var(--success))]',
    },
    failed: {
      icon: <CircleAlert className="h-3 w-3" aria-hidden />,
      text: t('ide.agent.check.failed'),
      tone: 'text-destructive',
    },
    skipped: {
      icon: <CircleSlash className="h-3 w-3" aria-hidden />,
      text: t('ide.agent.check.skipped'),
      tone: 'text-[hsl(var(--warning))]',
    },
  }[status];

  return (
    <p className={cn('flex items-center gap-1.5 px-1 text-[11px]', map.tone)}>
      {map.icon}
      {map.text}
    </p>
  );
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
}
