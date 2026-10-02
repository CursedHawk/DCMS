import { APP_JSON, pageIdFromPath, type SiteProblem } from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { AlertCircle, AlertTriangle, CheckCircle2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ID } from './canvas/tree';
import { useVisual } from './store';

/**
 * What `checkVisualSite` says about the site — the same report the AI agent gets and the one that
 * gates publishing. A problem on a node opens its page (or the shell) and selects it.
 */
export function ProblemsPanel({ problems, editor }: { problems: readonly SiteProblem[]; editor: Editor | null }) {
  const { t } = useTranslation();
  const setTarget = useVisual((s) => s.setTarget);

  const reveal = (problem: SiteProblem) => {
    const page = pageIdFromPath(problem.file);
    if (page) setTarget({ kind: 'page', id: page });
    else if (problem.file === APP_JSON && problem.nodeId) setTarget({ kind: 'shell' });
    if (!problem.nodeId || !editor) return;
    // The canvas loads the target on the next tick; select once it is there.
    setTimeout(() => {
      const match = findById(editor, problem.nodeId!);
      if (match) editor.select(match);
    }, 450);
  };

  if (problems.length === 0) {
    return (
      <div className="flex items-center gap-2 p-4 text-sm text-muted-foreground">
        <CheckCircle2 className="h-4 w-4 text-emerald-600" /> {t('visual.problems.none')}
      </div>
    );
  }
  return (
    <ul className="h-full divide-y overflow-y-auto text-sm">
      {problems.map((p, i) => (
        <li key={i}>
          <button type="button" onClick={() => reveal(p)} className="flex w-full items-start gap-2 px-3 py-2 text-left hover:bg-muted/60">
            {p.severity === 'error' ? (
              <AlertCircle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            ) : (
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-600" />
            )}
            <span className="min-w-0">
              <span className="block">{p.message}</span>
              <span className="block truncate text-xs text-muted-foreground">{p.file}</span>
            </span>
          </button>
        </li>
      ))}
    </ul>
  );
}

function findById(editor: Editor, nodeId: string) {
  const stack = [...(editor.getWrapper()?.components().models ?? [])];
  while (stack.length) {
    const c = stack.pop()!;
    if (c.get(ID) === nodeId) return c;
    stack.push(...c.components().models);
  }
  return undefined;
}
