import { Check, FastForward, X } from 'lucide-react';
import { useEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, cn } from '@dcms/ui';
import type { ApprovalDecision, ToolCall, ToolSpec } from '../../agent/contracts';
import { ALL_TOOLS } from './checkTools';
import { SANDBOX_TOOLS, SKILL_TOOLS, TENANT_TOOLS } from './tenantTools';
import { useVfs } from '../../site-source/vfs';
import { countLines, previewOf, previewTitle, PREVIEW_LINES, type ChangePreview } from './approval';

/**
 * The moment a person decides whether the agent may do something.
 *
 * <h3>What was wrong with the old card</h3>
 * <p>It listed <c>edit_file src/App.tsx</c> and offered Apply or Reject. That asks somebody to
 * approve a change to their site by reading the <i>name of the function</i> that makes it — and
 * because the name is always reasonable, the answer is always yes. An approval nobody can
 * meaningfully refuse is not a safety feature; it is a click people learn to make quickly, and
 * they carry that habit to the card that says <i>publish</i>.</p>
 *
 * <h3>What this one does</h3>
 * <ul>
 *   <li><b>Shows the diff.</b> Lines out, lines in, at the place in the file they happen.</li>
 *   <li><b>Three answers, not two.</b> "For the rest of this run" is how somebody who has read
 *   one card avoids being asked eight more times and reading none of them.</li>
 *   <li><b>Keyboard first.</b> The card takes focus when it appears: Enter allows, A allows for
 *   the run, Escape refuses. Reaching for the mouse mid-run is what makes people approve in
 *   batches without looking.</li>
 * </ul>
 */

const SPECS = new Map<string, ToolSpec<unknown>>(
  [
    ...(ALL_TOOLS as unknown as ToolSpec<unknown>[]),
    ...(SKILL_TOOLS as unknown as ToolSpec<unknown>[]),
    ...(SANDBOX_TOOLS as unknown as ToolSpec<unknown>[]),
    ...(TENANT_TOOLS as unknown as ToolSpec<unknown>[]),
  ].map((spec) => [spec.name, spec]),
);

export function ApprovalCard({
  calls,
  onDecide,
}: {
  calls: readonly ToolCall[];
  onDecide: (decision: ApprovalDecision) => void;
}) {
  const { t } = useTranslation();
  const files = useVfs((s) => s.files);
  const cardRef = useRef<HTMLDivElement>(null);
  // Bound once, so it must not close over a stale `onDecide`.
  const onDecideRef = useRef(onDecide);
  onDecideRef.current = onDecide;

  // Against the workspace as it stands now, which is the only state in which a preview of a
  // change that has not happened yet means anything.
  const previews = useMemo(
    () => calls.map((call) => previewOf(call, files, SPECS.get(call.name))),
    [calls, files],
  );
  const totals = useMemo(() => countLines(previews), [previews]);

  useEffect(() => {
    // Focus is for the announcement, not for the shortcuts: a `dialog` that takes focus is
    // read out when it appears, which is the whole point of raising one.
    cardRef.current?.focus();
  }, []);

  /*
   * Listened for on the document rather than on the card.
   *
   * <p>The run is stopped until this is answered, so the shortcut has to work wherever focus
   * happens to be — clicked into the transcript to re-read what led here, say. A handler bound
   * to the card only works while the card still holds focus, which is exactly the moment the
   * operator has stopped looking at it.</p>
   *
   * <p>Skipped while they are typing, so Enter in the composer is still a message and not an
   * approval of somebody's site being published.</p>
   */
  useEffect(() => {
    const onKeyDown = (ev: KeyboardEvent) => {
      const target = ev.target as HTMLElement | null;
      if (
        target instanceof HTMLInputElement ||
        target instanceof HTMLTextAreaElement ||
        target instanceof HTMLSelectElement ||
        target?.isContentEditable
      ) {
        return;
      }
      // A button inside the card already does the right thing on Enter or Space.
      if (ev.key === 'Enter' && target instanceof HTMLButtonElement) return;

      if (ev.key === 'Enter') decide('once');
      else if (ev.key === 'Escape') decide('deny');
      else if (ev.key === 'a' || ev.key === 'A') decide('run');
      else return;

      function decide(decision: ApprovalDecision) {
        ev.preventDefault();
        onDecideRef.current(decision);
      }
    };

    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, []);

  return (
    <div
      ref={cardRef}
      tabIndex={-1}
      // A dialog rather than a group: this asks for a decision before the run continues, and
      // `dialog` is what tells assistive tech that — including that Escape is expected to
      // dismiss it. Not `aria-modal`, because it deliberately does not trap focus; the operator
      // can still scroll the transcript above it to see what led here before answering.
      role="dialog"
      aria-label={t('ide.agent.approveTitle')}
      className="mx-3 mb-3 overflow-hidden rounded-md border border-primary/40 bg-primary/5 outline-none ring-offset-1 focus-visible:ring-2 focus-visible:ring-primary"
    >
      <div className="flex items-baseline gap-2 border-b border-primary/20 px-2.5 py-1.5">
        <p className="flex-1 text-xs font-medium">{t('ide.agent.approveTitle')}</p>
        {/* The number somebody actually weighs, before reading a single line. */}
        {(totals.added > 0 || totals.removed > 0) && (
          <span className="font-mono text-[10px] tabular-nums">
            <span className="text-emerald-600 dark:text-emerald-400">+{totals.added}</span>{' '}
            <span className="text-destructive">−{totals.removed}</span>
          </span>
        )}
      </div>

      <div className="max-h-72 overflow-y-auto">
        {previews.map((preview, i) => (
          <PreviewRow key={calls[i].id} preview={preview} />
        ))}
      </div>

      <div className="flex flex-wrap items-center gap-1.5 border-t border-primary/20 px-2.5 py-2">
        <Button size="sm" onClick={() => onDecide('once')}>
          <Check className="h-3.5 w-3.5" /> {t('ide.agent.allowOnce')}
          <Key>↵</Key>
        </Button>
        <Button size="sm" variant="outline" onClick={() => onDecide('run')}>
          <FastForward className="h-3.5 w-3.5" /> {t('ide.agent.allowForRun')}
          <Key>A</Key>
        </Button>
        <Button size="sm" variant="outline" onClick={() => onDecide('deny')}>
          <X className="h-3.5 w-3.5" /> {t('common.reject', 'Reject')}
          <Key>esc</Key>
        </Button>
      </div>
    </div>
  );
}

/** The shortcut, on the button that performs it — so nobody has to learn a list. */
function Key({ children }: { children: React.ReactNode }) {
  return (
    <kbd className="ml-1 rounded border border-current/30 px-1 font-mono text-[9px] opacity-60">
      {children}
    </kbd>
  );
}

function PreviewRow({ preview }: { preview: ChangePreview }) {
  const { t } = useTranslation();

  if (preview.kind === 'action') {
    return <p className="px-2.5 py-1.5 text-xs">{preview.text}</p>;
  }

  const removed = preview.kind === 'create' ? [] : preview.removed;
  const added = preview.kind === 'delete' ? [] : preview.added;
  const shown = [
    ...removed.slice(0, PREVIEW_LINES).map((text) => ({ sign: '-' as const, text })),
    ...added.slice(0, PREVIEW_LINES).map((text) => ({ sign: '+' as const, text })),
  ];
  const hidden =
    Math.max(0, removed.length - PREVIEW_LINES) + Math.max(0, added.length - PREVIEW_LINES);

  return (
    <div className="border-b border-primary/10 last:border-b-0">
      <p className="px-2.5 pt-1.5 font-mono text-[10px] text-muted-foreground">
        {preview.kind === 'create' && `${t('ide.agent.newFile')} `}
        {preview.kind === 'delete' && `${t('ide.agent.deleteFile')} `}
        {previewTitle(preview)}
      </p>
      <div className="px-2.5 pb-1.5 pt-1">
        {shown.map((line, i) => (
          <div
            key={i}
            className={cn(
              'flex gap-1.5 whitespace-pre-wrap break-all font-mono text-[10.5px] leading-snug',
              line.sign === '-'
                ? 'bg-destructive/10 text-destructive'
                : 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-400',
            )}
          >
            <span aria-hidden className="select-none opacity-50">
              {line.sign}
            </span>
            <span className="min-w-0 flex-1">{line.text || ' '}</span>
          </div>
        ))}
        {hidden > 0 && (
          <p className="pt-0.5 font-mono text-[10px] text-muted-foreground">
            {t('ide.agent.moreLines', { count: hidden })}
          </p>
        )}
      </div>
    </div>
  );
}
