import { describe, expect, it } from 'vitest';
import { countBySeverity, problemSummary, toProblems, type EsbuildMessage } from './problems';

const project = (path: string) => !path.startsWith('https://');

function message(over: Partial<EsbuildMessage> & { file?: string }): EsbuildMessage {
  const { file, ...rest } = over;
  return {
    text: 'Expected ";" but found "}"',
    location: file === undefined ? null : { file, line: 12, column: 4, lineText: '  return }' },
    ...rest,
  };
}

describe('toProblems', () => {
  it('turns a message into a place, with the editor 1-based column', () => {
    const [problem] = toProblems([message({ file: 'src/App.tsx' })], [], project);

    expect(problem).toEqual({
      severity: 'error',
      text: 'Expected ";" but found "}"',
      file: 'src/App.tsx',
      line: 12,
      // esbuild counts from zero; clicking a result would land one character early.
      column: 5,
      lineText: '  return }',
    });
  });

  /**
   * A dependency lives on the CDN, not in this project. Offering to open it makes an empty tab.
   */
  it('keeps a dependency problem readable but not clickable', () => {
    const [problem] = toProblems(
      [message({ file: 'https://esm.sh/react@19/react.mjs', text: 'Could not resolve "x"' })],
      [],
      project,
    );

    expect(problem.text).toBe('Could not resolve "x"');
    expect(problem.file).toBeUndefined();
    expect(problem.line).toBeUndefined();
  });

  it('keeps a message with no location at all', () => {
    const [problem] = toProblems([message({ text: 'Build failed' })], [], project);
    expect(problem).toEqual({ severity: 'error', text: 'Build failed' });
  });

  it('puts errors before warnings, because the build failed for one of them', () => {
    const problems = toProblems(
      [message({ file: 'src/a.ts', text: 'boom' })],
      [message({ file: 'src/b.ts', text: 'unused' })],
      project,
    );

    expect(problems.map((p) => p.severity)).toEqual(['error', 'warning']);
  });

  it('copes with a build that reported neither', () => {
    expect(toProblems(undefined, undefined, project)).toEqual([]);
  });
});

describe('problemSummary', () => {
  it('is the errors, which is what the preview overlay shows', () => {
    const problems = toProblems(
      [message({ text: 'one' }), message({ text: 'two' })],
      [message({ text: 'ignored' })],
      project,
    );

    expect(problemSummary(problems)).toBe('one\ntwo');
  });

  it('is nothing when only warnings were reported, so the preview is not covered up', () => {
    expect(problemSummary(toProblems([], [message({ text: 'unused' })], project))).toBeNull();
  });
});

describe('countBySeverity', () => {
  it('counts each kind for the badge', () => {
    const problems = toProblems(
      [message({ text: 'a' })],
      [message({ text: 'b' }), message({ text: 'c' })],
      project,
    );

    expect(countBySeverity(problems)).toEqual({ errors: 1, warnings: 2 });
  });
});
