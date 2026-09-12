import { describe, expect, it } from 'vitest';
import {
  applyAnchoredPatch,
  applyDeleteRange,
  applyInsertAt,
  applyReplaceRange,
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
