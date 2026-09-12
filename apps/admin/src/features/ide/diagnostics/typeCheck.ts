import { languageOf } from '../../site-source/paths';
import type { BuildProblem } from '../preview/problems';

/**
 * Real type checking, in the browser, for free.
 *
 * <p>esbuild strips types without reading them: it will happily bundle a call with the wrong
 * arguments, a misspelled prop and a `null` handed to something that cannot take one. Those are
 * most of the mistakes an agent actually makes, and until now nothing in the IDE caught them —
 * the build gate went green and the site broke at runtime.</p>
 *
 * <p><b>No new dependency and no bigger bundle.</b> The plan assumed this meant hosting `tsc` in
 * a worker, around 7 MB. It does not: Monaco is already here, its TypeScript worker is already
 * loaded for the editor's own IntelliSense, a model already exists for every non-binary file in
 * the VFS, and `setEagerModelSync(true)` already pushes all of them into that worker. The whole
 * project is type-checked continuously. Nothing was reading the answers.</p>
 *
 * <p>It also works with the preview <b>closed</b>, which the esbuild gate cannot do. A run that
 * was previously unverifiable is now at least type-checked.</p>
 *
 * <p>This half is pure on purpose. `checkTypes.ts` holds the part that talks to Monaco, because
 * importing Monaco drags an editor bundle into every test that touches any of this — the same
 * trap `useAgentSession` fell into via a barrel import.</p>
 */

/** Monaco's diagnostic shape, narrowed to the fields that survive the worker boundary. */
export interface TsDiagnostic {
  category: number;
  code: number;
  start?: number;
  length?: number;
  messageText: string | MessageChain;
}

export interface MessageChain {
  messageText: string;
  next?: MessageChain[];
}

/** TypeScript's `DiagnosticCategory`. */
const WARNING = 0;
const ERROR = 1;

/**
 * A diagnostic's message, chain and all.
 *
 * <p>TypeScript nests its better messages: "Type 'X' is not assignable to type 'Y'" carries a
 * child explaining <i>which property</i>, and that child is usually the useful half. Taking only
 * the top line is how a type error becomes unactionable.</p>
 *
 * <p>Depth-limited, because a deeply generic mismatch can nest far enough to fill a panel — and
 * an agent handed 4 KB of chained variance notes will spend a turn reading them.</p>
 */
export function flattenMessage(message: string | MessageChain, maxDepth = 3): string {
  if (typeof message === 'string') return message;
  const lines: string[] = [];
  const walk = (node: MessageChain, depth: number): void => {
    lines.push(depth === 0 ? node.messageText : `${'  '.repeat(depth)}${node.messageText}`);
    if (depth + 1 >= maxDepth) return;
    for (const child of node.next ?? []) walk(child, depth + 1);
  };
  walk(message, 0);
  return lines.join('\n');
}

/** Where in the file a character offset falls. 1-based, as the editor and `BuildProblem` want. */
export function positionAt(text: string, offset: number): { line: number; column: number } {
  const clamped = Math.max(0, Math.min(offset, text.length));
  let line = 1;
  let lineStart = 0;
  for (let i = 0; i < clamped; i++) {
    if (text.charCodeAt(i) === 10) {
      line++;
      lineStart = i + 1;
    }
  }
  return { line, column: clamped - lineStart + 1 };
}

/**
 * Turn one file's diagnostics into problems the IDE can navigate to.
 *
 * <p>Suggestions and plain messages are dropped. They are TypeScript's style hints — "this could
 * be a const", "prefer an optional chain" — and they are noise in a list whose job is to say
 * what is broken. Worse, the agent treats anything in the gate's report as something to fix, so
 * a suggestion here costs a repair turn and a billed model call to change nothing that
 * mattered.</p>
 */
export function toTypeProblems(
  path: string,
  text: string,
  diagnostics: readonly TsDiagnostic[],
): BuildProblem[] {
  const problems: BuildProblem[] = [];
  for (const diagnostic of diagnostics) {
    if (diagnostic.category !== ERROR && diagnostic.category !== WARNING) continue;
    const severity = diagnostic.category === ERROR ? 'error' : 'warning';
    const message = flattenMessage(diagnostic.messageText);
    if (diagnostic.start == null) {
      problems.push({ severity, text: `TS${diagnostic.code}: ${message}`, file: path });
      continue;
    }
    const { line, column } = positionAt(text, diagnostic.start);
    problems.push({
      severity,
      text: `TS${diagnostic.code}: ${message}`,
      file: path,
      line,
      column,
      lineText: text.split('\n')[line - 1],
    });
  }
  return problems;
}

/** Files the TypeScript worker knows about. Everything else has no types to check. */
export function isCheckable(path: string): boolean {
  const language = languageOf(path);
  return language === 'typescript' || language === 'javascript';
}

/**
 * Problems in `paths` that were not already there.
 *
 * <p><b>The reason the gate is usable at all.</b> Plenty of real sites carry type errors their
 * authors have decided to live with. Without a baseline, an agent asked to change a heading on
 * such a site fails its gate on somebody else's error, spends all three repair turns trying to
 * fix code it never touched, and reports a failure for work that was correct.</p>
 *
 * <p>Matched on code and message rather than position, so an error that merely moved down a few
 * lines because the run inserted something above it is still recognised as pre-existing.</p>
 */
export function newProblems(
  current: readonly BuildProblem[],
  baseline: ReadonlySet<string>,
): BuildProblem[] {
  return current.filter((problem) => !baseline.has(problemKey(problem)));
}

/**
 * What makes two problems "the same problem" across a run.
 *
 * <p>File, code and the first line of the message — deliberately <b>not</b> the position, so an
 * error that merely moved because the run inserted lines above it is still recognised as the
 * same error rather than reported as a new one.</p>
 */
export function problemKey(problem: BuildProblem): string {
  const firstLine = problem.text.split('\n')[0] ?? problem.text;
  return [problem.file ?? '', firstLine].join(' ');
}


/** One check's answer. `null` is "could not check", which is never "clean". */
export type CheckResult = readonly BuildProblem[] | null;

/**
 * The build gate's verdict, from however many checks could actually run.
 *
 * <p>Three states, and conflating any two of them is the failure this function exists to
 * prevent: <b>passed</b>, <b>failed</b>, and <b>nothing could be checked</b>. The last one
 * returns null, which the runtime reports as unverified — a run whose preview was closed and
 * whose type worker was unreachable has proved nothing, and saying it passed would be a lie the
 * operator has no way to catch.</p>
 *
 * <p>Warnings never fail the gate. They are worth showing and not worth a billed repair turn, so
 * they ride along in the report of a passing run.</p>
 */
export function combineVerdict(
  types: CheckResult,
  build: { ok: boolean; report: string } | null,
): { ok: boolean; report: string } | null {
  if (types === null && build === null) return null;

  const errors = (types ?? []).filter((p) => p.severity === 'error');
  const warnings = (types ?? []).filter((p) => p.severity === 'warning');

  const sections: string[] = [];
  if (errors.length > 0) {
    sections.push(`Type errors introduced by this run:\n${errors.map(formatProblem).join('\n')}`);
  }
  if (build && !build.ok && build.report) sections.push(build.report);
  if (sections.length === 0 && warnings.length > 0) {
    sections.push(`Type warnings:\n${warnings.map(formatProblem).join('\n')}`);
  }

  const ok = errors.length === 0 && (build === null || build.ok);
  return { ok, report: sections.join('\n\n') };
}

/** One problem, as a line the model can act on: where first, then what. */
export function formatProblem(problem: BuildProblem): string {
  const where = problem.file
    ? `${problem.file}${problem.line ? `:${problem.line}${problem.column ? `:${problem.column}` : ''}` : ''}`
    : '';
  return where ? `  ${where} — ${problem.text}` : `  ${problem.text}`;
}
