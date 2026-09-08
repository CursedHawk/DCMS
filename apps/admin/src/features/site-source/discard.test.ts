import { describe, expect, it } from 'vitest';
import { discardPlan } from './discard';
import type { GitChange } from './git';

function change(over: Partial<GitChange>): GitChange {
  return {
    path: 'src/App.tsx',
    status: 'modified',
    headContent: 'committed',
    draftContent: 'edited',
    truncated: false,
    ...over,
  };
}

describe('discardPlan', () => {
  it('removes a file that was added, because it has nothing to go back to', () => {
    expect(discardPlan(change({ status: 'added', headContent: null }))).toEqual({
      kind: 'delete',
      path: 'src/App.tsx',
    });
  });

  it('restores the committed content of a modified file', () => {
    expect(discardPlan(change({ status: 'modified' }))).toEqual({
      kind: 'write',
      path: 'src/App.tsx',
      content: 'committed',
    });
  });

  it('brings back a file that was deleted', () => {
    expect(discardPlan(change({ status: 'deleted', draftContent: null }))).toEqual({
      kind: 'write',
      path: 'src/App.tsx',
      content: 'committed',
    });
  });

  /**
   * The dangerous case. A truncated change has no content to restore, and writing the null back
   * would empty the file while the button reported success.
   */
  it('refuses when the committed content was not sent', () => {
    expect(discardPlan(change({ truncated: true })).kind).toBe('unavailable');
    expect(discardPlan(change({ headContent: null })).kind).toBe('unavailable');
  });

  it('still removes an added file even when its content was elided', () => {
    expect(discardPlan(change({ status: 'added', truncated: true })).kind).toBe('delete');
  });
});
