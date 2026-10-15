import { CircleAlert, CircleCheck, CircleSlash, RotateCcw, Square } from 'lucide-react';
import { useState } from 'react';
import { relativeTime } from '@dcms/core';
import { cn } from '../cn';
import { Button } from '../ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '../ui/popover';
import { Progress } from '../ui/progress';
import { ProgressRing } from '../ui/progress-ring';
import { Tooltip, TooltipContent, TooltipTrigger } from '../ui/tooltip';

export type JobStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Canceled';

/** The part of a job the indicator draws. Both consoles' job DTOs have at least this. */
export interface IndicatorJob {
  id: string;
  status: JobStatus;
  done: number;
  /** Null while the amount of work is unknown. */
  total: number | null;
  cancelable: boolean;
  cancelRequested: boolean;
  retryable: boolean;
  createdAt: string;
  finishedAt: string | null;
}

export interface JobsLabels {
  title: string;
  /** Given the number of queued and running jobs, e.g. "3 background jobs in progress". */
  active: (count: number) => string;
  running: string;
  queued: string;
  recent: string;
  empty: string;
  viewAll: string;
  cancel: string;
  retry: string;
  loading: string;
  status: (status: JobStatus) => string;
}

/** A job's own fraction done, or null when it cannot say. */
export function jobFraction(job: Pick<IndicatorJob, 'status' | 'done' | 'total'>): number | null {
  if (job.status === 'Succeeded') return 1;
  return job.total && job.total > 0 ? Math.min(1, job.done / job.total) : null;
}

/**
 * What the ring shows: the mean of the running jobs that know their size. Null (a turning arc)
 * when something is active but none of it can say how far along it is; 0 when idle.
 */
export function overallProgress(active: readonly IndicatorJob[]): number | null {
  if (active.length === 0) return 0;
  const known = active
    .filter((j) => j.status === 'Running')
    .map(jobFraction)
    .filter((f): f is number => f !== null);
  if (known.length === 0) return null;
  return known.reduce((a, b) => a + b, 0) / known.length;
}

const finishedIcon = {
  Succeeded: { Icon: CircleCheck, tone: 'text-[hsl(var(--success))]' },
  Failed: { Icon: CircleAlert, tone: 'text-destructive' },
  Canceled: { Icon: CircleSlash, tone: 'text-muted-foreground' },
} as const;

/**
 * The jobs ring beside the bell, shared by both consoles (ADR 0024).
 *
 * <p>The ring is the average progress of everything running, so one glance answers "is my
 * import still going"; the popover splits it into running, queued and recently finished, with
 * cancel and retry where the job allows them. What is each app's is where the rows come from and
 * how a job's title is produced — the same split as the bell.</p>
 */
export function JobsIndicator<T extends IndicatorJob>({
  enabled = true,
  active,
  recent,
  isLoading,
  labels,
  locale,
  renderItem,
  onCancel,
  onRetry,
  onOpenItem,
  onViewAll,
}: {
  enabled?: boolean;
  /** Queued and running, newest first. */
  active: readonly T[];
  /** Recently finished, newest first. */
  recent: readonly T[];
  isLoading?: boolean;
  labels: JobsLabels;
  locale?: string;
  renderItem: (job: T) => { title: string; detail?: string };
  onCancel?: (job: T) => void;
  onRetry?: (job: T) => void;
  onOpenItem?: (job: T) => void;
  onViewAll?: () => void;
}) {
  const [open, setOpen] = useState(false);
  if (!enabled) return null;

  const running = active.filter((j) => j.status === 'Running');
  const queued = active.filter((j) => j.status === 'Queued');
  const progress = overallProgress(active);
  const label = active.length > 0 ? labels.active(active.length) : labels.title;

  const row = (job: T) => (
    <JobRow
      key={job.id}
      job={job}
      text={renderItem(job)}
      locale={locale}
      labels={labels}
      onCancel={onCancel ? () => onCancel(job) : undefined}
      onRetry={onRetry ? () => onRetry(job) : undefined}
      onOpen={
        onOpenItem
          ? () => {
              setOpen(false);
              onOpenItem(job);
            }
          : undefined
      }
    />
  );

  return (
    <Popover open={open} onOpenChange={setOpen}>
      {/* Both triggers are asChild Slots on the one Button; see NotificationBell for why. */}
      <Tooltip>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={label} data-testid="jobs-indicator">
              <ProgressRing
                value={progress}
                className={cn(active.length === 0 && 'opacity-50')}
                label={label}
              >
                {active.length > 0 ? (active.length > 9 ? '9+' : active.length) : undefined}
              </ProgressRing>
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent>{label}</TooltipContent>
      </Tooltip>

      <PopoverContent align="end" className="w-[min(24rem,calc(100vw-1.5rem))] p-0">
        <div className="border-b px-3 py-2 text-sm font-semibold">{labels.title}</div>

        <div className="max-h-[28rem] overflow-y-auto">
          {isLoading ? (
            <p className="px-3 py-8 text-center text-sm text-muted-foreground">{labels.loading}</p>
          ) : active.length === 0 && recent.length === 0 ? (
            <p className="px-3 py-8 text-center text-sm text-muted-foreground">{labels.empty}</p>
          ) : (
            <>
              <Section title={labels.running} items={running} render={row} />
              <Section title={labels.queued} items={queued} render={row} />
              <Section title={labels.recent} items={recent} render={row} />
            </>
          )}
        </div>

        {onViewAll ? (
          <div className="border-t px-3 py-2">
            <button
              type="button"
              onClick={() => {
                setOpen(false);
                onViewAll();
              }}
              className="w-full text-center text-xs text-muted-foreground transition-colors hover:text-foreground"
            >
              {labels.viewAll}
            </button>
          </div>
        ) : null}
      </PopoverContent>
    </Popover>
  );
}

function Section<T>({ title, items, render }: { title: string; items: readonly T[]; render: (item: T) => React.ReactNode }) {
  if (items.length === 0) return null;
  return (
    <section>
      <h3 className="bg-muted/40 px-3 py-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
        {title}
      </h3>
      {items.map(render)}
    </section>
  );
}

export function JobRow({
  job,
  text,
  locale,
  labels,
  onCancel,
  onRetry,
  onOpen,
}: {
  job: IndicatorJob;
  text: { title: string; detail?: string };
  locale?: string;
  labels: Pick<JobsLabels, 'cancel' | 'retry' | 'status'>;
  onCancel?: () => void;
  onRetry?: () => void;
  onOpen?: () => void;
}) {
  const fraction = jobFraction(job);
  const finished = job.status === 'Succeeded' || job.status === 'Failed' || job.status === 'Canceled';
  const icon = finished ? finishedIcon[job.status as keyof typeof finishedIcon] : null;
  const percent = job.status === 'Running' && fraction !== null ? ` · ${Math.round(fraction * 100)} %` : '';

  const body = (
    <>
      <p className="truncate text-sm">{text.title}</p>
      <p
        className={cn(
          'mt-0.5 truncate text-xs text-muted-foreground',
          job.status === 'Failed' && 'text-destructive',
        )}
      >
        {text.detail ? `${text.detail}${percent}` : `${labels.status(job.status)}${percent}`}
      </p>
      {job.status === 'Running' ? (
        fraction !== null ? (
          <Progress value={fraction} className="mt-1.5" label={text.title} />
        ) : (
          <div className="mt-1.5 h-1.5 w-full overflow-hidden rounded-full bg-muted">
            <div className="h-full w-1/3 rounded-full bg-primary/60 motion-safe:animate-pulse" />
          </div>
        )
      ) : null}
      <p className="mt-1 text-[11px] text-muted-foreground/70">
        {relativeTime(job.finishedAt ?? job.createdAt, locale)}
      </p>
    </>
  );

  return (
    <div className="flex items-start gap-2 border-b px-3 py-2.5 last:border-b-0" data-testid="job-row">
      {icon ? <icon.Icon className={cn('mt-0.5 h-4 w-4 shrink-0', icon.tone)} aria-hidden /> : null}

      {onOpen ? (
        <button type="button" onClick={onOpen} className="min-w-0 flex-1 text-left">
          {body}
        </button>
      ) : (
        <div className="min-w-0 flex-1">{body}</div>
      )}

      {onCancel && job.cancelable && !job.cancelRequested ? (
        <IconAction label={labels.cancel} onClick={onCancel}>
          <Square className="h-3.5 w-3.5" aria-hidden />
        </IconAction>
      ) : null}
      {onRetry && job.retryable ? (
        <IconAction label={labels.retry} onClick={onRetry}>
          <RotateCcw className="h-3.5 w-3.5" aria-hidden />
        </IconAction>
      ) : null}
    </div>
  );
}

function IconAction({ label, onClick, children }: { label: string; onClick: () => void; children: React.ReactNode }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          onClick={onClick}
          aria-label={label}
          className="rounded p-1 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {children}
        </button>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}
