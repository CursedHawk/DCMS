import { describe, expect, it } from 'vitest';
import { diffStat, totalStat } from './diff';

const lines = (...values: string[]) => values.join('\n');

describe('diffStat', () => {
  it('reports nothing for identical content', () => {
    expect(diffStat('a\nb\nc', 'a\nb\nc')).toEqual({ added: 0, removed: 0, approximate: false });
  });

  it('reports nothing for two empty files', () => {
    expect(diffStat('', '')).toEqual({ added: 0, removed: 0, approximate: false });
  });

  it('counts every line of an added file', () => {
    expect(diffStat(null, lines('a', 'b', 'c'))).toEqual({
      added: 3,
      removed: 0,
      approximate: false,
    });
  });

  it('counts every line of a deleted file', () => {
    expect(diffStat(lines('a', 'b'), null)).toEqual({ added: 0, removed: 2, approximate: false });
  });

  it('treats a missing file on both sides as no change', () => {
    expect(diffStat(null, null)).toEqual({ added: 0, removed: 0, approximate: false });
  });

  it('counts an inserted line', () => {
    expect(diffStat(lines('a', 'c'), lines('a', 'b', 'c'))).toMatchObject({ added: 1, removed: 0 });
  });

  it('counts a deleted line', () => {
    expect(diffStat(lines('a', 'b', 'c'), lines('a', 'c'))).toMatchObject({ added: 0, removed: 1 });
  });

  it('counts a changed line as one added and one removed', () => {
    expect(diffStat(lines('a', 'b', 'c'), lines('a', 'B', 'c'))).toMatchObject({
      added: 1,
      removed: 1,
    });
  });

  it('counts edits scattered through a file', () => {
    const before = lines('a', 'b', 'c', 'd', 'e');
    const after = lines('a', 'x', 'c', 'd', 'e', 'f');
    expect(diffStat(before, after)).toMatchObject({ added: 2, removed: 1 });
  });

  it('does not count a moved block as unchanged', () => {
    const before = lines('a', 'b', 'c');
    const after = lines('c', 'a', 'b');
    const stat = diffStat(before, after);
    expect(stat.added).toBeGreaterThan(0);
    expect(stat.removed).toBeGreaterThan(0);
  });

  it('ignores a trailing newline rather than reporting a phantom line', () => {
    expect(diffStat('a\nb', 'a\nb\n')).toEqual({ added: 0, removed: 0, approximate: false });
  });

  it('normalizes CRLF, so a line-ending change alone is not a diff', () => {
    expect(diffStat('a\r\nb\r\n', 'a\nb\n')).toEqual({ added: 0, removed: 0, approximate: false });
  });

  it('counts an emptied file', () => {
    expect(diffStat(lines('a', 'b'), '')).toMatchObject({ added: 0, removed: 2 });
  });

  it('counts a file filled from empty', () => {
    expect(diffStat('', lines('a', 'b'))).toMatchObject({ added: 2, removed: 0 });
  });

  it('handles a large edit at the end of a long identical prefix', () => {
    const prefix = Array.from({ length: 5000 }, (_, i) => `line ${i}`);
    const before = prefix.join('\n');
    const after = [...prefix, 'new one', 'new two'].join('\n');
    expect(diffStat(before, after)).toEqual({ added: 2, removed: 0, approximate: false });
  });

  it('handles a repeated line, where naive matching drifts', () => {
    const before = lines('x', 'x', 'x');
    const after = lines('x', 'x', 'x', 'x');
    expect(diffStat(before, after)).toMatchObject({ added: 1, removed: 0 });
  });

  it('falls back to whole-file counts when the files are unrelated', () => {
    // Beyond the edit-distance cap the exact split stops being informative, so
    // the counts become the file sizes and say so.
    const before = Array.from({ length: 4000 }, (_, i) => `old ${i}`).join('\n');
    const after = Array.from({ length: 4000 }, (_, i) => `new ${i}`).join('\n');
    const stat = diffStat(before, after);
    expect(stat).toEqual({ added: 4000, removed: 4000, approximate: true });
  });
});

describe('totalStat', () => {
  it('sums counts across files', () => {
    expect(
      totalStat([
        { added: 3, removed: 1, approximate: false },
        { added: 2, removed: 5, approximate: false },
      ]),
    ).toEqual({ added: 5, removed: 6, approximate: false });
  });

  it('is approximate if any file was', () => {
    expect(
      totalStat([
        { added: 1, removed: 0, approximate: false },
        { added: 9, removed: 9, approximate: true },
      ]),
    ).toMatchObject({ approximate: true });
  });

  it('returns zero for nothing', () => {
    expect(totalStat([])).toEqual({ added: 0, removed: 0, approximate: false });
  });
});
