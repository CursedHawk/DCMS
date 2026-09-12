import { Trash2 } from 'lucide-react';
import { useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { clearOutput, outputLines, subscribeOutput, type OutputLine } from './output';
import { PanelEmpty } from './chrome';
import { clockTime } from './clockTime';

/**
 * Everything the workspace did on the author's behalf.
 *
 * <p>These used to be toasts and nothing else, which is the right shape for "this just
 * happened" and the wrong one for "what happened while I was reading". A toast that has faded
 * is gone; this is where it went.</p>
 */
export function OutputView() {
  const { t } = useTranslation();
  const lines = useSyncExternalStore(subscribeOutput, outputLines, outputLines);

  if (lines.length === 0) {
    return <PanelEmpty title={t('ide.panel.output.empty')} />;
  }

  return (
    <div className="flex h-full flex-col">
      <div className="flex shrink-0 justify-end px-2 py-1">
        <button
          type="button"
          onClick={clearOutput}
          className="flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px] text-muted-foreground hover:bg-accent hover:text-foreground"
        >
          <Trash2 className="h-3 w-3" aria-hidden />
          {t('ide.panel.clear')}
        </button>
      </div>
      <ol className="min-h-0 flex-1 overflow-auto px-2 pb-2">
        {lines.map((line) => (
          <Line key={line.id} line={line} />
        ))}
      </ol>
    </div>
  );
}

function Line({ line }: { line: OutputLine }) {
  return (
    <li className="flex gap-2 py-px font-mono text-[11px] leading-relaxed">
      <span className="shrink-0 tabular-nums text-muted-foreground/70">{clockTime(line.at)}</span>
      {/* The channel, not a severity chip: which part of the workspace spoke is the thing that
          makes a mixed log readable, and severity is already carried by the colour. */}
      <span className="w-[4.5rem] shrink-0 truncate text-muted-foreground">{line.channel}</span>
      <span
        className={cn(
          'min-w-0 flex-1 whitespace-pre-wrap break-words',
          line.level === 'error' && 'text-destructive',
          line.level === 'warn' && 'text-[hsl(var(--warning))]',
          line.level === 'info' && 'text-foreground',
        )}
      >
        {line.text}
      </span>
    </li>
  );
}
