import { describe, expect, it } from 'vitest';
import type { BuildProblem } from '../preview/problems';
import {
  combineVerdict,
  flattenMessage,
  isCheckable,
  newProblems,
  formatProblem,
  positionAt,
  problemKey,
  toTypeProblems,
  type TsDiagnostic,
} from './typeCheck';

const ERROR = 1;
const WARNING = 0;
const SUGGESTION = 2;
const MESSAGE = 3;

describe('flattenMessage', () => {
  it('passes a plain string through', () => {
    expect(flattenMessage('Cannot find name "foo".')).toBe('Cannot find name "foo".');
  });

  it('keeps the nested explanation, which is usually the useful half', () => {
    const flat = flattenMessage({
      messageText: "Type 'A' is not assignable to type 'B'.",
      next: [{ messageText: "Property 'id' is missing in type 'A'." }],
    });
    expect(flat).toBe(
      "Type 'A' is not assignable to type 'B'.\n  Property 'id' is missing in type 'A'.",
    );
  });

  it('stops at the depth limit rather than filling a panel with variance notes', () => {
    const deep = {
      messageText: 'one',
      next: [{ messageText: 'two', next: [{ messageText: 'three', next: [{ messageText: 'four' }] }] }],
    };
    expect(flattenMessage(deep)).toBe('one\n  two\n    three');
    expect(flattenMessage(deep, 1)).toBe('one');
  });
});

describe('positionAt', () => {
  const text = 'const a = 1;\nconst b = 2;\nconst c = 3;\n';

  it('is 1-based on both axes, matching the editor', () => {
    expect(positionAt(text, 0)).toEqual({ line: 1, column: 1 });
  });

  it('finds a position on a later line', () => {
    expect(positionAt(text, text.indexOf('const b'))).toEqual({ line: 2, column: 1 });
    expect(positionAt(text, text.indexOf('b = 2'))).toEqual({ line: 2, column: 7 });
  });

  it('clamps rather than returning nonsense for an offset past the end', () => {
    const at = positionAt(text, 9999);
    expect(at.line).toBe(4);
    expect(at.column).toBe(1);
  });
});

describe('toTypeProblems', () => {
  const text = 'const a: number = "x";\nexport default a;\n';

  const diagnostic = (over: Partial<TsDiagnostic> = {}): TsDiagnostic => ({
    category: ERROR,
    code: 2322,
    start: text.indexOf('"x"'),
    length: 3,
    messageText: "Type 'string' is not assignable to type 'number'.",
    ...over,
  });

  it('maps an error to a navigable place in the file', () => {
    const [problem] = toTypeProblems('src/a.ts', text, [diagnostic()]);
    expect(problem).toMatchObject({
      severity: 'error',
      file: 'src/a.ts',
      line: 1,
      column: 19,
      lineText: 'const a: number = "x";',
    });
    expect(problem.text).toContain('TS2322');
  });

  it('keeps warnings, which esbuild discarded entirely', () => {
    const [problem] = toTypeProblems('src/a.ts', text, [diagnostic({ category: WARNING })]);
    expect(problem.severity).toBe('warning');
  });

  it('drops suggestions and messages', () => {
    // Style hints cost a repair turn and a billed model call to change nothing that mattered.
    const problems = toTypeProblems('src/a.ts', text, [
      diagnostic({ category: SUGGESTION }),
      diagnostic({ category: MESSAGE }),
    ]);
    expect(problems).toEqual([]);
  });

  it('still reports a diagnostic with no position, without inventing one', () => {
    const [problem] = toTypeProblems('src/a.ts', text, [
      diagnostic({ start: undefined, code: 2688, messageText: "Cannot find type definition file." }),
    ]);
    expect(problem.file).toBe('src/a.ts');
    expect(problem.line).toBeUndefined();
    expect(problem.column).toBeUndefined();
  });
});

describe('isCheckable', () => {
  it('takes the files the TypeScript worker actually holds', () => {
    for (const path of ['src/App.tsx', 'src/a.ts', 'src/b.js', 'src/c.jsx', 'src/d.mjs']) {
      expect(isCheckable(path)).toBe(true);
    }
  });

  it('leaves everything else alone', () => {
    for (const path of ['styles/app.css', 'index.html', 'package.json', 'README.md', 'logo.png']) {
      expect(isCheckable(path)).toBe(false);
    }
  });
});

describe('newProblems', () => {
  const problem = (over: Partial<BuildProblem> = {}): BuildProblem => ({
    severity: 'error',
    text: 'TS2322: Type "string" is not assignable to type "number".',
    file: 'src/a.ts',
    line: 1,
    column: 19,
    ...over,
  });

  it('subtracts an error that was already there', () => {
    const baseline = new Set([problemKey(problem())]);
    expect(newProblems([problem()], baseline)).toEqual([]);
  });

  it('still subtracts it after the run pushed it down the file', () => {
    // The whole point of keying on code and message rather than position: an agent that
    // inserted an import above somebody else's long-standing error must not be blamed for it.
    const baseline = new Set([problemKey(problem({ line: 1 }))]);
    expect(newProblems([problem({ line: 14, column: 19 })], baseline)).toEqual([]);
  });

  it('reports an error the run actually introduced', () => {
    const baseline = new Set([problemKey(problem())]);
    const introduced = problem({ text: 'TS2554: Expected 2 arguments, but got 1.', file: 'src/b.ts' });
    expect(newProblems([problem(), introduced], baseline)).toEqual([introduced]);
  });

  it('reports everything when the baseline is empty', () => {
    expect(newProblems([problem()], new Set())).toHaveLength(1);
  });

  it('tells two errors in the same file apart', () => {
    const other = problem({ text: 'TS2554: Expected 2 arguments, but got 1.' });
    const baseline = new Set([problemKey(problem())]);
    expect(newProblems([problem(), other], baseline)).toEqual([other]);
  });

  it('keys on the first line, so a chained message does not defeat the match', () => {
    const chained = problem({ text: `${problem().text}\n  Property 'id' is missing.` });
    const baseline = new Set([problemKey(problem())]);
    expect(newProblems([chained], baseline)).toEqual([]);
  });
});

describe('combineVerdict', () => {
  const error: BuildProblem = {
    severity: 'error',
    text: 'TS2322: Type "string" is not assignable to type "number".',
    file: 'src/a.ts',
    line: 3,
    column: 9,
  };
  const warning: BuildProblem = { ...error, severity: 'warning', text: 'TS6133: "x" is declared but never used.' };

  it('returns null only when neither check could run', () => {
    // The run is then reported as unverified. Saying it passed would be a claim nobody can check.
    expect(combineVerdict(null, null)).toBeNull();
  });

  it('passes when both checks ran and found nothing', () => {
    expect(combineVerdict([], { ok: true, report: '' })).toEqual({ ok: true, report: '' });
  });

  it('passes on types alone when the build could not run', () => {
    // The preview is closed. Type checking still proves something, and it is not nothing.
    expect(combineVerdict([], null)?.ok).toBe(true);
  });

  it('passes on the build alone when the type checker could not be reached', () => {
    expect(combineVerdict(null, { ok: true, report: '' })?.ok).toBe(true);
  });

  it('fails on a type error even though the bundle is fine', () => {
    // The whole reason this check exists: esbuild strips types without reading them.
    const verdict = combineVerdict([error], { ok: true, report: '' });
    expect(verdict?.ok).toBe(false);
    expect(verdict?.report).toContain('src/a.ts:3:9');
    expect(verdict?.report).toContain('TS2322');
  });

  it('fails on a build error even though the types are fine', () => {
    const verdict = combineVerdict([], { ok: false, report: 'Unexpected token' });
    expect(verdict?.ok).toBe(false);
    expect(verdict?.report).toContain('Unexpected token');
  });

  it('reports both when both failed', () => {
    const verdict = combineVerdict([error], { ok: false, report: 'Unexpected token' });
    expect(verdict?.report).toContain('TS2322');
    expect(verdict?.report).toContain('Unexpected token');
  });

  it('does not fail a run on warnings, but still shows them', () => {
    // Worth seeing; not worth a billed repair turn.
    const verdict = combineVerdict([warning], { ok: true, report: '' });
    expect(verdict?.ok).toBe(true);
    expect(verdict?.report).toContain('TS6133');
  });

  it('leads with the errors when warnings are also present', () => {
    const verdict = combineVerdict([warning, error], null);
    expect(verdict?.ok).toBe(false);
    expect(verdict?.report).toContain('TS2322');
    expect(verdict?.report).not.toContain('TS6133');
  });
});

describe('formatProblem', () => {
  it('puts the place first, because that is what gets acted on', () => {
    expect(formatProblem({ severity: 'error', text: 'Boom', file: 'src/a.ts', line: 2, column: 7 })).toBe(
      '  src/a.ts:2:7 — Boom',
    );
  });

  it('degrades cleanly when there is no location to offer', () => {
    expect(formatProblem({ severity: 'error', text: 'Boom' })).toBe('  Boom');
    expect(formatProblem({ severity: 'error', text: 'Boom', file: 'src/a.ts' })).toBe('  src/a.ts — Boom');
  });
});
