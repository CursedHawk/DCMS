import { describe, expect, it } from 'vitest';
import {
  applyAnchoredPatch,
  applyDeleteRange,
  applyInsertAt,
  applyReplaceRange,
  rebaseAnchoredPatch,
  type EditFailure,
  type EditSuccess,
} from './edits';

const ok = (r: ReturnType<typeof applyAnchoredPatch>) => r as EditSuccess;
const no = (r: ReturnType<typeof applyAnchoredPatch>) => r as EditFailure;

describe('applyAnchoredPatch', () => {
  it('replaces a unique anchor', () => {
    const r = ok(applyAnchoredPatch('const a = 1;\nconst b = 2;', 'a = 1', 'a = 99'));
    expect(r.content).toBe('const a = 99;\nconst b = 2;');
  });

  it('reports the touched line range', () => {
    const r = ok(applyAnchoredPatch('one\ntwo\nthree', 'two', 'two\nextra'));
    expect(r.touched).toEqual({ start: 2, end: 3 });
  });

  it('refuses an ambiguous anchor and names both offsets', () => {
    // Replacing "the first occurrence" in a file with repeated structure corrupts it silently
    // and still compiles; refusing is what turns that into something the model can fix.
    const r = no(applyAnchoredPatch('x = 1;\nx = 1;', 'x = 1', 'x = 2'));
    expect(r.reason).toBe('anchor-ambiguous');
    expect(r.message).toMatch(/offsets 0 and 7/);
  });

  it('replaces every occurrence when asked explicitly', () => {
    const r = ok(applyAnchoredPatch('x;\nx;', 'x', 'y', { replaceAll: true }));
    expect(r.content).toBe('y;\ny;');
  });

  it('refuses a missing anchor with advice, not just a failure', () => {
    const r = no(applyAnchoredPatch('abc', 'zzz', 'q'));
    expect(r.reason).toBe('anchor-missing');
    expect(r.message).toMatch(/Re-read the file/);
  });

  it('refuses an empty anchor', () => {
    expect(no(applyAnchoredPatch('abc', '', 'x')).reason).toBe('anchor-missing');
  });

  it('refuses a no-op edit rather than reporting success', () => {
    expect(no(applyAnchoredPatch('abc', 'abc', 'abc')).reason).toBe('unchanged');
  });

  it('handles a multi-line anchor', () => {
    const r = ok(applyAnchoredPatch('a\nb\nc', 'a\nb', 'z'));
    expect(r.content).toBe('z\nc');
  });

  it('treats the anchor literally, not as a pattern', () => {
    const r = ok(applyAnchoredPatch('a.b and axb', 'a.b', 'Q'));
    expect(r.content).toBe('Q and axb');
  });
});

describe('applyReplaceRange', () => {
  const file = 'one\ntwo\nthree\nfour';

  it('replaces an inclusive 1-based range', () => {
    expect(ok(applyReplaceRange(file, 2, 3, 'TWO')).content).toBe('one\nTWO\nfour');
  });

  it('replaces a single line', () => {
    expect(ok(applyReplaceRange(file, 1, 1, 'ONE')).content).toBe('ONE\ntwo\nthree\nfour');
  });

  it('expands a line into several', () => {
    const r = ok(applyReplaceRange(file, 2, 2, 'a\nb'));
    expect(r.content).toBe('one\na\nb\nthree\nfour');
    expect(r.touched).toEqual({ start: 2, end: 3 });
  });

  it('refuses rather than clamping an out-of-range end', () => {
    // A read clamps because nearby code is still useful; a write that clamps deletes lines
    // nobody named.
    const r = no(applyReplaceRange(file, 2, 99, 'x'));
    expect(r.reason).toBe('out-of-range');
    expect(r.message).toContain('4 lines');
  });

  it('refuses a start before the file', () => {
    expect(no(applyReplaceRange(file, 0, 1, 'x')).reason).toBe('out-of-range');
  });

  it('refuses an inverted range', () => {
    expect(no(applyReplaceRange(file, 3, 2, 'x')).message).toMatch(/before startLine/);
  });

  it('refuses non-integer lines', () => {
    expect(no(applyReplaceRange(file, 1.5, 2, 'x')).reason).toBe('out-of-range');
  });
});

describe('applyDeleteRange', () => {
  it('removes the named lines', () => {
    expect(ok(applyDeleteRange('a\nb\nc', 2, 2)).content).toBe('a\nc');
  });

  it('can empty a file', () => {
    expect(ok(applyDeleteRange('a\nb', 1, 2)).content).toBe('');
  });
});

describe('applyInsertAt', () => {
  it('inserts before the given line', () => {
    expect(ok(applyInsertAt('a\nb', 2, 'mid')).content).toBe('a\nmid\nb');
  });

  it('prepends at line 1', () => {
    expect(ok(applyInsertAt('a', 1, 'first')).content).toBe('first\na');
  });

  it('appends one past the end', () => {
    expect(ok(applyInsertAt('a\nb', 3, 'last')).content).toBe('a\nb\nlast');
  });

  it('refuses beyond one past the end', () => {
    expect(no(applyInsertAt('a\nb', 4, 'x')).reason).toBe('out-of-range');
  });

  it('refuses line 0', () => {
    expect(no(applyInsertAt('a', 0, 'x')).reason).toBe('out-of-range');
  });

  it('inserts multiple lines and reports the range', () => {
    const r = ok(applyInsertAt('a\nb', 2, 'x\ny'));
    expect(r.content).toBe('a\nx\ny\nb');
    expect(r.touched).toEqual({ start: 2, end: 3 });
  });
});

describe('rebaseAnchoredPatch', () => {
  const base = 'import a from "a";\n\nexport function App() {\n  return <h1>Hello</h1>;\n}\n';

  it('applies a patch whose anchor survived an edit elsewhere in the file', () => {
    // The human added an import above while the model was working on the heading.
    const current = 'import a from "a";\nimport b from "b";\n\nexport function App() {\n  return <h1>Hello</h1>;\n}\n';
    const r = ok(rebaseAnchoredPatch({ base, current, oldText: '<h1>Hello</h1>', newText: '<h1>Hi</h1>' }));
    expect(r.content).toBe(current.replace('<h1>Hello</h1>', '<h1>Hi</h1>'));
    // Reported against the current file, where the line actually is now.
    expect(r.touched.start).toBe(5);
  });

  it('refuses when the other edit changed the anchor itself', () => {
    const current = base.replace('Hello', 'Welcome');
    const r = no(rebaseAnchoredPatch({ base, current, oldText: '<h1>Hello</h1>', newText: '<h1>Hi</h1>' }));
    expect(r.reason).toBe('anchor-missing');
    expect(r.message).toMatch(/same lines/);
  });

  it('refuses when the other edit made the anchor ambiguous', () => {
    const current = base + 'export const Copy = () => <h1>Hello</h1>;\n';
    const r = no(rebaseAnchoredPatch({ base, current, oldText: '<h1>Hello</h1>', newText: '<h1>Hi</h1>' }));
    expect(r.reason).toBe('anchor-ambiguous');
  });

  it('does not let a human typing the anchor rescue an edit that was wrong against what the model read', () => {
    // Not in the version the model read, so the edit would have failed on its own merits.
    const current = base.replace('Hello', 'Hello</h1><h1>Goodbye');
    const r = no(rebaseAnchoredPatch({ base, current, oldText: '<h1>Goodbye</h1>', newText: 'x' }));
    expect(r.reason).toBe('anchor-missing');
  });

  it('never applies replace_all to a version the model has not seen', () => {
    const current = base + '// Hello again\n';
    const r = no(
      rebaseAnchoredPatch({ base, current, oldText: 'Hello', newText: 'Hi', replaceAll: true }),
    );
    expect(r.reason).toBe('hash-mismatch');
  });
});
