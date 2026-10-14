import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { fileStem, slugify, useAutoSlug } from './slug';

describe('slugify', () => {
  it('folds diacritics and collapses everything else into single hyphens', () => {
    expect(slugify('Příliš žluťoučký kůň')).toBe('prilis-zlutoucky-kun');
    expect(slugify('  Hello, World!  ')).toBe('hello-world');
    expect(slugify('...')).toBe('');
  });

  it('cuts to the limit without leaving a trailing hyphen', () => {
    expect(slugify('abc def', 4)).toBe('abc');
  });

  it('takes a file name without its extension, and only the extension', () => {
    expect(slugify(fileStem('Hero shot_02.final.JPG'))).toBe('hero-shot-02-final');
  });
});

describe('useAutoSlug', () => {
  it('follows the source until the user types, then keeps what they typed', () => {
    const { result, rerender } = renderHook(({ source }) => useAutoSlug(source), {
      initialProps: { source: 'Spring launch' },
    });
    expect(result.current[0]).toBe('spring-launch');

    rerender({ source: 'Summer launch' });
    expect(result.current[0]).toBe('summer-launch');

    act(() => result.current[1]('launch'));
    rerender({ source: 'Autumn launch' });
    expect(result.current[0]).toBe('launch');

    // Emptied is still theirs: backspacing must not refill the field.
    act(() => result.current[1](''));
    expect(result.current[0]).toBe('');

    act(() => result.current[1](null));
    expect(result.current[0]).toBe('autumn-launch');
  });
});
