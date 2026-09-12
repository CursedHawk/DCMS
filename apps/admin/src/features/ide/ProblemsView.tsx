import { AlertTriangle, CircleAlert, CircleCheck, EyeOff } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, cn } from '@dcms/ui';
import { useVfs } from '../site-source';
import type { BuildProblem } from './preview/problems';

/**
 * What the last build had to say, as a list of places rather than a paragraph.
 *
 * <p>Before this, a build failure was a blob of text in an overlay on the preview: it named a
 * file and a line, and finding them was the author's job. Warnings were not reported at all —
 * the worker discarded them.</p>
 *
 * <p><b>The build only runs while the preview is on</b>, so with it hidden this list is empty
 * for a reason that has nothing to do with the code. Saying so, with the switch to turn it back
 * on, is the difference between "no problems" and "nothing has been checked".</p>
 */
export function ProblemsView({
  problems,
  building,
  previewEnabled,
  onEnablePreview,
}: {
  problems: readonly BuildProblem[];
  building: boolean;
  previewEnabled: boolean;
  onEnablePreview: () => void;
}) {
  const { t } = useTranslation();
  const revealAt = useVfs((s) => s.revealAt);

  /*
   * With the preview off, the BUILD has not run — but the type checker has, because Monaco's
   * worker is attached either way. So this is no longer an empty screen: it lists what types
   * say, and notes that the build is the half still missing.
   *
   * The note stays visible above a non-empty list rather than only replacing it. "Three
   * problems" and "three problems, and the bundler has not been consulted" are different
   * claims, and the second is the true one.
   */
  const buildUnchecked = !previewEnabled;

  if (problems.length === 0) {
    if (buildUnchecked) {
      return (
        <Empty
          icon={EyeOff}
          title={t('ide.problems.previewOff')}
          description={t('ide.problems.previewOffHint')}
        >
          <Button variant="outline" size="sm" onClick={onEnablePreview}>
            {t('ide.showPreview')}
          </Button>
        </Empty>
      );
    }
    return building ? (
      <p className="p-2 text-xs text-muted-foreground">{t('ide.building')}</p>
    ) : (
      <Empty icon={CircleCheck} title={t('ide.problems.none')} />
    );
  }

  return (
    <div className="flex h-full flex-col">
      {buildUnchecked && (
        <div className="flex shrink-0 items-center gap-1.5 border-b bg-muted/40 px-2 py-1 text-[11px] text-muted-foreground">
          <EyeOff className="h-3 w-3 shrink-0" aria-hidden />
          <span className="flex-1">{t('ide.problems.typesOnly')}</span>
          <button
            type="button"
            onClick={onEnablePreview}
            className="font-medium text-primary hover:underline"
          >
            {t('ide.showPreview')}
          </button>
        </div>
      )}
      <ul className="min-h-0 flex-1 overflow-auto py-1">
        {problems.map((problem, i) => {
          const Icon = problem.severity === 'error' ? CircleAlert : AlertTriangle;
          const navigable = problem.file != null && problem.line != null;

          const body = (
            <>
              <Icon
                className={cn(
                  'mt-0.5 h-3.5 w-3.5 shrink-0',
                  problem.severity === 'error' ? 'text-destructive' : 'text-[hsl(var(--warning))]',
                )}
                aria-hidden
              />
              <span className="min-w-0 flex-1">
                <span className="block break-words text-xs">{problem.text}</span>
                {problem.file && (
                  <span className="block truncate font-mono text-[10px] text-muted-foreground">
                    {problem.file}
                    {problem.line != null && `:${problem.line}`}
                  </span>
                )}
                {/* The offending line, verbatim. Often the whole answer, and it saves the trip. */}
                {problem.lineText && (
                  <code className="mt-0.5 block truncate rounded bg-muted px-1 py-0.5 font-mono text-[10px]">
                    {problem.lineText.trim()}
                  </code>
                )}
              </span>
            </>
          );

          return (
            <li key={`${problem.file ?? ''}:${problem.line ?? 0}:${i}`}>
              {navigable ? (
                <button
                  type="button"
                  onClick={() => revealAt(problem.file!, problem.line!, problem.column ?? 1)}
                  className="flex w-full items-start gap-1.5 px-2 py-1 text-left hover:bg-accent/50"
                >
                  {body}
                </button>
              ) : (
                // A message from a dependency, or one esbuild gave no location for. Rendered as
                // text rather than as a dead button that looks clickable and does nothing.
                <div className="flex items-start gap-1.5 px-2 py-1">{body}</div>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

function Empty({
  icon: Icon,
  title,
  description,
  children,
}: {
  icon: typeof CircleCheck;
  title: string;
  description?: string;
  children?: React.ReactNode;
}) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-2 px-4 text-center">
      <Icon className="h-6 w-6 text-muted-foreground" aria-hidden />
      <p className="text-xs font-medium">{title}</p>
      {description && <p className="text-[11px] text-muted-foreground">{description}</p>}
      {children}
    </div>
  );
}
