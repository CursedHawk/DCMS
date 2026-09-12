import { FileDiff, RotateCcw, Undo2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, cn } from '@dcms/ui';
import type { FileChange } from '../../agent/transaction';
import { useVfs } from '../../site-source/vfs';

/**
 * What the run actually changed, and how to undo it.
 *
 * <p>An agent that edits your files without a review step is asking you to trust it on the
 * strength of its own description. This is the other half: the same list a source-control panel
 * shows, built from the run's transaction rather than from the server, so it is available
 * immediately and before anything is committed.</p>
 *
 * <p><b>Accept is not a button here, and that is deliberate.</b> The changes are already in the
 * workspace — the agent writes through the same VFS the editor does, so "accept" would be a
 * no-op with a reassuring label, which is the worst kind of control. The real actions are the
 * destructive ones: revert a file, or revert the lot. Anything not reverted is kept, which is
 * what already happened.</p>
 */
export function ChangeReview({
  changes,
  onRevert,
  onRevertAll,
}: {
  changes: readonly FileChange[];
  onRevert: (path: string) => void;
  onRevertAll: () => void;
}) {
  const { t } = useTranslation();
  const openDiff = useVfs((s) => s.openDiff);

  if (changes.length === 0) return null;

  return (
    <section className="shrink-0 border-t bg-card/60">
      <header className="flex items-center gap-2 px-3 py-1.5">
        <FileDiff className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden />
        <h2 className="flex-1 text-[11px] font-semibold text-foreground">
          {t('ide.agent.changed', { count: changes.length })}
        </h2>
        <Button size="sm" variant="ghost" className="h-6 px-1.5 text-[11px]" onClick={onRevertAll}>
          <RotateCcw className="h-3 w-3" aria-hidden />
          {t('ide.agent.revertAll')}
        </Button>
      </header>

      <ul className="max-h-40 overflow-auto px-1.5 pb-1.5">
        {changes.map((change) => (
          <li key={change.path} className="group flex items-center gap-1.5 rounded px-1.5 py-0.5">
            <Kind kind={change.kind} />
            <button
              type="button"
              onClick={() =>
                openDiff({
                  path: change.path,
                  status: change.kind === 'created' ? 'added' : change.kind,
                  original: change.before,
                  modified: change.after,
                })
              }
              title={t('ide.agent.showDiff')}
              className="min-w-0 flex-1 truncate text-left font-mono text-[11px] text-foreground hover:underline"
            >
              {change.path}
            </button>
            <button
              type="button"
              onClick={() => onRevert(change.path)}
              aria-label={t('ide.agent.revertFile', { path: change.path })}
              title={t('ide.agent.revertFile', { path: change.path })}
              className="shrink-0 rounded p-0.5 text-muted-foreground opacity-0 hover:text-destructive focus-visible:opacity-100 group-hover:opacity-100"
            >
              <Undo2 className="h-3 w-3" aria-hidden />
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}

/**
 * One letter, in source-control's own vocabulary.
 *
 * <p>A for added, M for modified, D for deleted — the letters git uses, because anybody reading
 * this list already knows them and a bespoke set of icons would be a second thing to learn for
 * no gain.</p>
 */
function Kind({ kind }: { kind: FileChange['kind'] }) {
  const letter = kind === 'created' ? 'A' : kind === 'deleted' ? 'D' : 'M';
  return (
    <span
      aria-hidden
      className={cn(
        'w-3 shrink-0 text-center font-mono text-[10px] font-semibold',
        kind === 'created' && 'text-[hsl(var(--success))]',
        kind === 'deleted' && 'text-destructive',
        kind === 'modified' && 'text-[hsl(var(--warning))]',
      )}
    >
      {letter}
    </span>
  );
}
