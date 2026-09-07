import { describe, expect, it } from 'vitest';
import {
  buildConflictText,
  hasUnresolvedMarkers,
  isFullyResolved,
  markerLines,
  resolvedContent,
  resolvedCount,
  type ConflictFile,
} from './conflict';

const file: ConflictFile = { path: 'src/App.tsx', mine: 'const a = 1;', theirs: 'const a = 2;' };

describe('buildConflictText', () => {
  it('writes both sides between git-shaped markers', () => {
    const text = buildConflictText(file, 'feature', 'release');
    expect(text).toBe(
      ['<<<<<<< feature', 'const a = 1;', '=======', 'const a = 2;', '>>>>>>> release'].join('\n'),
    );
  });

  it('returns the text unmarked when both sides already agree', () => {
    // git only reports a path as conflicted, not which hunks; identical content is possible
    // (whitespace-only merges), and marking it up would invent a decision.
    expect(buildConflictText({ path: 'a', mine: 'same', theirs: 'same' })).toBe('same');
  });

  it('shows an empty side rather than skipping it, when one side deleted the file', () => {
    // "The other person deleted this" is the case a reader most needs to SEE rather than infer.
    const text = buildConflictText({ path: 'a', mine: 'kept', theirs: null }, 'mine', 'theirs');
    expect(text).toContain('kept');
    expect(text).toContain('=======\n\n>>>>>>> theirs');
  });
});

describe('hasUnresolvedMarkers', () => {
  it('finds each marker', () => {
    expect(hasUnresolvedMarkers('<<<<<<< a')).toBe(true);
    expect(hasUnresolvedMarkers('x\n=======\ny')).toBe(true);
    expect(hasUnresolvedMarkers('>>>>>>> b')).toBe(true);
  });

  it('says no for text that has been merged', () => {
    expect(hasUnresolvedMarkers('const a = 3;')).toBe(false);
  });

  it('does not mistake ordinary content for a marker', () => {
    // Markdown rules, shell heredocs and diff output all contain runs of these characters.
    expect(hasUnresolvedMarkers('=========')).toBe(false);
    expect(hasUnresolvedMarkers('<<<<<<<<')).toBe(false);
    expect(hasUnresolvedMarkers('a ======= b')).toBe(false);
    expect(hasUnresolvedMarkers('# ====== section ======')).toBe(false);
  });

  it('accepts a bare marker line with nothing after it', () => {
    expect(hasUnresolvedMarkers('a\n=======\nb')).toBe(true);
  });
});

describe('markerLines', () => {
  it('numbers the markers from one, so the editor can jump to the first', () => {
    const text = ['<<<<<<< mine', 'a', '=======', 'b', '>>>>>>> theirs'].join('\n');
    expect(markerLines(text)).toEqual([1, 3, 5]);
  });

  it('returns nothing for resolved text', () => {
    expect(markerLines('done')).toEqual([]);
  });
});

describe('resolvedContent', () => {
  it('returns the chosen side', () => {
    expect(resolvedContent(file, { kind: 'mine' })).toBe('const a = 1;');
    expect(resolvedContent(file, { kind: 'theirs' })).toBe('const a = 2;');
  });

  it('returns the hand-merged text', () => {
    expect(resolvedContent(file, { kind: 'manual', content: 'const a = 3;' })).toBe('const a = 3;');
  });

  it('returns null for a side that deleted the file — which the API reads as a delete', () => {
    expect(resolvedContent({ path: 'a', mine: null, theirs: 'x' }, { kind: 'mine' })).toBeNull();
  });
});

describe('isFullyResolved', () => {
  const files: ConflictFile[] = [file, { path: 'b.css', mine: '.a{}', theirs: '.b{}' }];

  it('is false while any file is unanswered', () => {
    expect(isFullyResolved(files, { 'src/App.tsx': { kind: 'mine' } })).toBe(false);
  });

  it('is true once every file has a side', () => {
    expect(
      isFullyResolved(files, { 'src/App.tsx': { kind: 'mine' }, 'b.css': { kind: 'theirs' } }),
    ).toBe(true);
  });

  it('refuses a hand merge that still contains markers', () => {
    // Committing markers produces source that does not parse and a build that fails minutes
    // later — by which point the merge is already on the branch.
    expect(
      isFullyResolved([file], {
        'src/App.tsx': { kind: 'manual', content: '<<<<<<< mine\na\n=======\nb\n>>>>>>> theirs' },
      }),
    ).toBe(false);
  });

  it('accepts a hand merge with the markers taken out', () => {
    expect(
      isFullyResolved([file], { 'src/App.tsx': { kind: 'manual', content: 'const a = 3;' } }),
    ).toBe(true);
  });

  it('accepts an empty conflict list', () => {
    expect(isFullyResolved([], {})).toBe(true);
  });
});

describe('resolvedCount', () => {
  it('counts only the files that can actually be committed', () => {
    const files: ConflictFile[] = [file, { path: 'b.css', mine: '.a{}', theirs: '.b{}' }];
    expect(
      resolvedCount(files, {
        'src/App.tsx': { kind: 'mine' },
        'b.css': { kind: 'manual', content: '<<<<<<< x\n=======\n>>>>>>> y' },
      }),
    ).toBe(1);
  });
});
