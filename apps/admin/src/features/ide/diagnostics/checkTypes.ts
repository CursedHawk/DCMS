import { languageOf, uriOf } from '../../site-source/paths';
import type { BuildProblem } from '../preview/problems';
import { isCheckable, problemKey, toTypeProblems, type TsDiagnostic } from './typeCheck';

/**
 * The half that talks to Monaco's TypeScript worker.
 *
 * <p>Separated from `typeCheck.ts` so the mapping and diffing logic stays testable without
 * pulling an editor bundle into the test runner. See that file for why this costs nothing.</p>
 *
 * <p><b>Monaco is imported lazily, inside the call.</b> A static import would make the editor a
 * dependency of the agent runtime's module graph, and that graph is loaded by tests with no DOM
 * to give it — which is precisely the failure `useAgentSession` already carries a comment about.
 * In the browser the IDE has loaded Monaco long before anything here runs, so the dynamic import
 * resolves from the module cache and costs nothing.</p>
 */

/**
 * Ask the worker about a set of files.
 *
 * <p>Scoped to the paths given rather than the whole project, which is the difference between a
 * check the agent can afford after every turn and one it cannot. Semantic diagnostics still see
 * the whole program — the worker holds every model — so an error caused here but surfacing in a
 * file the run never touched is <i>found</i>; it is simply reported against the file that has
 * it, which is where somebody has to go and fix it.</p>
 *
 * <p>Returns `null` when the worker cannot be reached at all, which is a different fact from
 * "no problems" and is the one thing the gate must not confuse.</p>
 */
export async function checkTypes(
  paths: readonly string[],
  timeoutMs = DEFAULT_TIMEOUT_MS,
): Promise<BuildProblem[] | null> {
  const checkable = paths.filter(isCheckable);
  if (checkable.length === 0) return [];
  return withTimeout(collect(checkable), timeoutMs);
}

/**
 * Bound every answer in time.
 *
 * <p><b>Learned the hard way.</b> Without this the gate simply never returned when the worker
 * did not answer — no error, no timeout, just a run parked on "Checking the build" until
 * somebody pressed Stop. A check that cannot finish has to become "unknown", the same as one
 * that fails to start; the one thing it must never become is "clean".</p>
 */
async function withTimeout<T>(work: Promise<T>, ms: number): Promise<T | null> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      work,
      new Promise<null>((resolve) => {
        timer = setTimeout(() => resolve(null), ms);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

/** How long the worker gets before its silence is reported as "not checked". */
const DEFAULT_TIMEOUT_MS = 10_000;

async function collect(checkable: readonly string[]): Promise<BuildProblem[] | null> {
  try {
    const { monaco, setupMonaco } = await import('../../site-source/monaco-setup');
    setupMonaco();
    const problems: BuildProblem[] = [];

    for (const path of checkable) {
      const uri = monaco.Uri.parse(uriOf(path));
      const model = monaco.editor.getModel(uri);
      // A file with no model was never synced to the worker, so asking about it would report
      // an empty program rather than the truth.
      if (!model) continue;

      // The model's own text, not the VFS copy: the diagnostics' offsets are against what the
      // worker was given, and `workspace.read` truncates a long file, which would shift every
      // line number past the cut.
      const text = model.getValue();

      const getWorker =
        languageOf(path) === 'javascript'
          ? await monaco.languages.typescript.getJavaScriptWorker()
          : await monaco.languages.typescript.getTypeScriptWorker();
      const client = await getWorker(uri);

      const [syntactic, semantic] = await Promise.all([
        client.getSyntacticDiagnostics(uri.toString()),
        client.getSemanticDiagnostics(uri.toString()),
      ]);

      problems.push(
        ...toTypeProblems(path, text, [
          ...(syntactic as TsDiagnostic[]),
          ...(semantic as TsDiagnostic[]),
        ]),
      );
    }

    return problems;
  } catch {
    // The worker failed or was torn down mid-question. Say "unknown", never "clean".
    return null;
  }
}

/**
 * A snapshot of what was already wrong, for `newProblems` to subtract.
 *
 * <p>Taken over the whole project rather than the changed files, because at the moment it has to
 * be taken nobody knows yet which files the run will touch.</p>
 *
 * <p>Started but <b>not awaited</b> by the caller: it runs while the first model turn is in
 * flight, so on any run that does real work it costs nothing. Past `maxFiles` it gives up and
 * returns null — the gate then reports types as unchecked rather than drowning the run in a
 * project's existing debt.</p>
 */
export async function baselineTypeProblems(
  paths: readonly string[],
  maxFiles = 400,
): Promise<ReadonlySet<string> | null> {
  const checkable = paths.filter(isCheckable);
  if (checkable.length > maxFiles) return null;
  // A longer leash than a scoped check: this is the whole project, and it runs while the first
  // model turn is in flight rather than while anyone is waiting on it.
  const problems = await checkTypes(checkable, 20_000);
  if (problems === null) return null;
  return new Set(problems.map(problemKey));
}
