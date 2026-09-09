import { Square } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, cn } from '@dcms/ui';
import type { Step, StepStatus, ToolStep } from './steps';
import { WorkCard } from './WorkCard';

/**
 * The record of a run, read top to bottom.
 *
 * <p>Not a chat. Chat bubbles alternate sides and cap their width, which is the wrong shape for
 * something whose interesting content is a list of changes with fields and diffs in it — and
 * they spend the horizontal room those need on whitespace. This reads as a worklog: the
 * operator's own words set flush left against a rule, the answer as ordinary prose, and every
 * step the agent took hanging off a spine that says, at a glance, what ran and how it went.</p>
 */
export function Transcript({
  steps,
  running,
  elapsed,
  onStop,
  selectedStepId,
  onSelectStep,
  compact,
}: {
  steps: readonly Step[];
  running: boolean;
  /** Seconds since the current run started, for the run header. */
  elapsed?: number;
  onStop: () => void;
  selectedStepId?: string | null;
  onSelectStep?: (step: ToolStep | null) => void;
  /** Dock: detail opens inline. Full page: detail opens in the inspector. */
  compact?: boolean;
}) {
  const { t } = useTranslation();

  return (
    <div className="relative">
      {/*
        The spine. One hairline behind every node, drawn once rather than per row so it never
        breaks between steps — the continuity is the information: this was one run.
      */}
      <div className="absolute bottom-2 left-[3px] top-2 w-px bg-border" aria-hidden />

      <ol className="space-y-2.5">
        {steps.map((step) => (
          <li key={step.id} className="relative pl-5">
            <Node status={nodeStatus(step)} kind={step.kind} />
            <StepBody
              step={step}
              compact={compact}
              expanded={selectedStepId === step.id}
              onToggle={() =>
                onSelectStep?.(
                  step.kind === 'tool' && selectedStepId !== step.id ? (step as ToolStep) : null,
                )
              }
            />
          </li>
        ))}
      </ol>

      {running ? (
        <div className="relative mt-3 pl-5">
          <span
            className="absolute left-0 top-[7px] h-[7px] w-[7px] animate-pulse bg-primary"
            aria-hidden
          />
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            <span>{t('assistant.working', { count: steps.filter(isTool).length })}</span>
            {elapsed !== undefined ? (
              <span className="font-mono tabular-nums">{format(elapsed)}</span>
            ) : null}
            <Button variant="ghost" size="sm" className="ml-auto h-6 px-2" onClick={onStop}>
              <Square className="h-3 w-3" aria-hidden />
              {t('assistant.stop')}
            </Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}

function StepBody({
  step,
  expanded,
  onToggle,
  compact,
}: {
  step: Step;
  expanded: boolean;
  onToggle: () => void;
  compact?: boolean;
}) {
  if (step.kind === 'user') {
    return (
      <div className="border-l-2 border-primary/70 pl-2.5">
        <p className="whitespace-pre-wrap font-medium">{step.text}</p>
        {step.files?.length ? (
          <p className="mt-0.5 font-mono text-[11px] text-muted-foreground">
            {step.files.join(', ')}
          </p>
        ) : null}
      </div>
    );
  }

  if (step.kind === 'assistant') {
    // Full measure, no bubble: this is the answer, and it is the thing being read.
    return <p className="max-w-[68ch] whitespace-pre-wrap leading-relaxed">{step.text}</p>;
  }

  if (step.kind === 'error') {
    return (
      <p className="rounded-md border border-destructive/40 bg-destructive/10 px-2.5 py-1.5 text-destructive">
        {step.text}
      </p>
    );
  }

  return <WorkCard step={step} expanded={expanded} onToggle={onToggle} compact={compact} />;
}

/**
 * A square, not a dot.
 *
 * <p>Circles read as messages; squares read as steps in a build, which is what these are. The
 * colour is the only status indicator that survives being glanced at, so it carries the state
 * rather than decorating it.</p>
 */
function Node({ status, kind }: { status: StepStatus | 'said' | 'asked'; kind: Step['kind'] }) {
  return (
    <span
      className={cn(
        'absolute left-0 top-[7px] h-[7px] w-[7px]',
        kind === 'user' && 'bg-foreground',
        kind === 'assistant' && 'border border-border bg-background',
        status === 'running' && 'animate-pulse bg-primary',
        status === 'awaiting' && 'bg-[hsl(var(--warning))]',
        status === 'ok' && 'bg-[hsl(var(--success))]',
        status === 'error' && 'bg-destructive',
        status === 'declined' && 'bg-muted-foreground/40',
      )}
      aria-hidden
    />
  );
}

const isTool = (step: Step): step is ToolStep => step.kind === 'tool';

function nodeStatus(step: Step): StepStatus | 'said' | 'asked' {
  if (step.kind === 'tool') return step.status;
  if (step.kind === 'error') return 'error';
  return step.kind === 'user' ? 'asked' : 'said';
}

function format(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = Math.floor(seconds % 60);
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}
