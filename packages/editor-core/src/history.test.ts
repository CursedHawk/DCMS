import { describe, expect, it } from 'vitest';
import { canRedo, canUndo, initHistory, push, redo, undo } from './history';

describe('history (undo/redo)', () => {
  it('pushes and undoes/redoes', () => {
    let h = initHistory(1);
    h = push(h, 2);
    h = push(h, 3);
    expect(h.present).toBe(3);
    expect(canUndo(h)).toBe(true);

    h = undo(h);
    expect(h.present).toBe(2);
    h = undo(h);
    expect(h.present).toBe(1);
    expect(canUndo(h)).toBe(false);

    h = redo(h);
    expect(h.present).toBe(2);
    expect(canRedo(h)).toBe(true);
  });

  it('clears the redo stack on a new push', () => {
    let h = initHistory('a');
    h = push(h, 'b');
    h = undo(h); // present 'a', future ['b']
    h = push(h, 'c');
    expect(h.present).toBe('c');
    expect(canRedo(h)).toBe(false);
  });

  it('is a no-op to undo/redo at the ends', () => {
    const h = initHistory(0);
    expect(undo(h)).toBe(h);
    expect(redo(h)).toBe(h);
  });

  it('caps undo depth at the limit', () => {
    let h = initHistory(0);
    for (let i = 1; i <= 5; i++) h = push(h, i, 3);
    // past capped at 3 entries
    expect(h.past.length).toBeLessThanOrEqual(3);
    expect(h.present).toBe(5);
  });
});
