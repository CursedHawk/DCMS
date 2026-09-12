import { useEffect, useState } from 'react';
import { useVfs } from '../../site-source/vfs';
import type { BuildProblem } from '../preview/problems';
import { checkTypes } from './checkTypes';
import { isCheckable } from './typeCheck';

/**
 * Type problems for the files the author is actually working on.
 *
 * <p>The Problems panel used to hold esbuild's output and nothing else, which meant two gaps.
 * It listed only what stops a bundle — a wrong argument or a misspelled prop compiles fine and
 * was never mentioned. And <b>with the preview closed it was empty</b>, for a reason that had
 * nothing to do with the code. Type checking has neither limitation: the worker is always there.</p>
 *
 * <p>Scoped to open tabs rather than the whole project. Semantic diagnostics still see the whole
 * program, so an error anywhere is <i>found</i> — it is reported against the file that has it,
 * and this lists the ones in files the author can see. A project-wide list would be a different
 * feature (and a slower one) than "what is wrong with what I am looking at".</p>
 */
export function useTypeProblems(enabled = true): BuildProblem[] {
  const [problems, setProblems] = useState<BuildProblem[]>([]);
  const rev = useVfs((s) => s.rev);
  const openTabs = useVfs((s) => s.openTabs);

  // The tab list identity changes on every reorder; its content is what matters here.
  const paths = openTabs.filter(isCheckable).join('\n');

  useEffect(() => {
    if (!enabled || paths.length === 0) {
      setProblems([]);
      return;
    }

    let cancelled = false;
    // Debounced, because `rev` ticks on every keystroke and half-typed code is full of errors
    // that are not mistakes. Long enough to let a line be finished, short enough to feel live.
    const timer = setTimeout(() => {
      void checkTypes(paths.split('\n')).then((found) => {
        // A check that could not run leaves the last list in place rather than clearing it:
        // blanking the panel would read as "no problems", which is the one thing it must not say.
        if (!cancelled && found !== null) setProblems(found);
      });
    }, 600);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [enabled, paths, rev]);

  return problems;
}
