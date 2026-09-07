import { describe, expect, it } from 'vitest';
import { cn } from './cn';

describe('cn', () => {
  it('joins classes and drops falsy ones', () => {
    expect(cn('a', false && 'b', undefined, 'c')).toBe('a c');
  });

  it('lets a later conflicting utility win — this is what makes className overridable', () => {
    // Without tailwind-merge both survive and the winner is whichever CSS rule came last
    // in the bundle, which is not something a caller can reason about.
    expect(cn('px-4', 'px-6')).toBe('px-6');
    expect(cn('bg-primary', 'bg-destructive')).toBe('bg-destructive');
  });

  it('keeps non-conflicting utilities from the same group', () => {
    expect(cn('px-4', 'py-2')).toBe('px-4 py-2');
  });
});
