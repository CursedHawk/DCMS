import { ChevronRight, Paperclip } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from '@tanstack/react-router';
import { cn } from '@dcms/ui';
import { fieldChanges, prettyPayload, type ToolStep } from './steps';

/**
 * One thing the agent did, as a line you can open.
 *
 * <p>Collapsed it is a sentence — "Created draft autumn-26" — because a transcript of twelve
 * function names is a transcript nobody reads. Opened it is the arguments as fields, the
 * before-and-after where the tool reported one, and the raw payloads underneath for the reader
 * who wants to see exactly what went over the wire.</p>
 */
export function WorkCard({
  step,
  expanded,
  onToggle,
  compact,
}: {
  step: ToolStep;
  expanded: boolean;
  onToggle: () => void;
  /** In the dock the detail opens inline; on the full page it opens in the inspector. */
  compact?: boolean;
}) {
  const { t } = useTranslation();
  const duration =
    step.endedAt && step.startedAt ? Math.max(0, step.endedAt - step.startedAt) : undefined;

  return (
    <div>
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={expanded}
        className={cn(
          'group flex w-full items-baseline gap-2 rounded-sm py-0.5 text-left',
          'hover:bg-accent/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        )}
      >
        <ChevronRight
          className={cn(
            'mt-0.5 h-3 w-3 shrink-0 text-muted-foreground transition-transform',
            expanded && 'rotate-90',
          )}
          aria-hidden
        />
        <span
          className={cn(
            'min-w-0 flex-1 truncate text-[13px]',
            step.status === 'error' && 'text-destructive',
            step.status === 'declined' && 'text-muted-foreground line-through',
          )}
        >
          {step.label}
        </span>
        {step.status === 'awaiting' ? (
          <span className="shrink-0 text-[11px] font-medium text-[hsl(var(--warning))]">
            {t('assistant.needsYou')}
          </span>
        ) : null}
        {duration !== undefined && duration > 400 ? (
          <span className="shrink-0 font-mono text-[11px] text-muted-foreground">
            {(duration / 1000).toFixed(1)}s
          </span>
        ) : null}
      </button>

      {expanded && compact ? <WorkDetail step={step} /> : null}
    </div>
  );
}

/**
 * The inside of a call.
 *
 * <p>Arguments as a field list rather than a JSON blob: the operator reading this is checking
 * whether the right thing happened to the right item, and `{"id":"8f2c…","data":{…}}` makes
 * them parse rather than read. The JSON is still one click away, because when a call goes wrong
 * the exact bytes are the only thing that helps.</p>
 */
export function WorkDetail({ step }: { step: ToolStep }) {
  const { t } = useTranslation();
  const [raw, setRaw] = useState(false);
  const changes = fieldChanges(step);
  const args = Object.entries(step.input);

  return (
    <div className="mt-1.5 space-y-3 rounded-md border bg-muted/30 p-3 text-[13px]">
      <div className="flex items-center gap-2">
        <code className="font-mono text-[11px] text-muted-foreground">{step.name}</code>
        {step.risk === 'dangerous' ? (
          <span className="rounded-sm bg-destructive/10 px-1.5 py-px font-mono text-[10px] text-destructive">
            {t('assistant.riskDangerous')}
          </span>
        ) : null}
        <button
          type="button"
          onClick={() => setRaw((was) => !was)}
          className="ml-auto font-mono text-[11px] text-muted-foreground underline-offset-2 hover:underline"
        >
          {raw ? t('assistant.showFields') : t('assistant.showRaw')}
        </button>
      </div>

      {changes.length > 0 && !raw ? (
        <div className="space-y-1.5">
          <p className="text-xs text-muted-foreground">{t('assistant.whatChanged')}</p>
          <dl className="space-y-1.5">
            {changes.map((change) => (
              <div key={change.field} className="grid grid-cols-[8rem_1fr] gap-2">
                <dt className="truncate font-mono text-[11px] text-muted-foreground">
                  {change.field}
                </dt>
                <dd className="min-w-0 space-y-0.5">
                  <p className="truncate text-muted-foreground line-through">
                    {render(change.before)}
                  </p>
                  <p className="break-words">{render(change.after)}</p>
                </dd>
              </div>
            ))}
          </dl>
        </div>
      ) : null}

      {args.length > 0 && !raw && changes.length === 0 ? (
        <dl className="space-y-1">
          {args.map(([key, value]) => (
            <div key={key} className="grid grid-cols-[8rem_1fr] gap-2">
              <dt className="truncate font-mono text-[11px] text-muted-foreground">{key}</dt>
              <dd className="min-w-0 break-words">{render(value)}</dd>
            </div>
          ))}
        </dl>
      ) : null}

      {raw ? (
        <Payload label={t('assistant.sent')} value={JSON.stringify(step.input, null, 2)} />
      ) : null}

      {step.error ? (
        <p className="rounded border border-destructive/40 bg-destructive/10 p-2 text-destructive">
          {step.error}
        </p>
      ) : null}

      {raw && step.result ? (
        <Payload label={t('assistant.returned')} value={prettyPayload(step.result)} />
      ) : null}

      <Records step={step} />
    </div>
  );
}

/** Long payloads are cut rather than scrolled: a transcript should not contain a second scroller. */
function Payload({ label, value }: { label: string; value: string }) {
  const { t } = useTranslation();
  const [all, setAll] = useState(false);
  const long = value.length > 1200;
  return (
    <div className="space-y-1">
      <p className="text-xs text-muted-foreground">{label}</p>
      <pre className="overflow-x-auto rounded bg-background p-2 font-mono text-[11px] leading-relaxed">
        {all || !long ? value : `${value.slice(0, 1200)}…`}
      </pre>
      {long ? (
        <button
          type="button"
          onClick={() => setAll((was) => !was)}
          className="font-mono text-[11px] text-muted-foreground underline-offset-2 hover:underline"
        >
          {all ? t('assistant.showLess') : t('assistant.showAll')}
        </button>
      ) : null}
    </div>
  );
}

/**
 * Links to whatever the call touched.
 *
 * <p>The point of a work card is to end at the thing itself. An id the operator has to copy
 * into a search box is a dead end wearing the clothes of a detail view.</p>
 */
function Records({ step }: { step: ToolStep }) {
  const { t } = useTranslation();
  const contentId = typeof step.input.id === 'string' ? step.input.id : undefined;
  const media = step.name.includes('media');

  if (!contentId && !media) return null;
  return (
    <div className="flex flex-wrap gap-2 pt-0.5">
      {contentId && !media ? (
        <Link
          to={'/content' as string}
          search={{ item: contentId } as never}
          className="inline-flex items-center gap-1 text-xs text-primary underline-offset-2 hover:underline"
        >
          {t('assistant.openItem')}
        </Link>
      ) : null}
      {media ? (
        <Link
          to={'/media' as string}
          className="inline-flex items-center gap-1 text-xs text-primary underline-offset-2 hover:underline"
        >
          <Paperclip className="h-3 w-3" aria-hidden />
          {t('assistant.openMedia')}
        </Link>
      ) : null}
    </div>
  );
}

function render(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'string') return value.length > 400 ? `${value.slice(0, 400)}…` : value;
  return JSON.stringify(value);
}
