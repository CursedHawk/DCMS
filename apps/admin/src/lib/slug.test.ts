import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ApiError } from '@dcms/core';
import { fileStem, isSlugTaken, randomSlugId, slugify, useAutoSlug } from './slug';

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

it('a random id is eight slug-safe characters, and not the same twice', () => {
  expect(randomSlugId()).toMatch(/^[a-z0-9]{8}$/);
  expect(randomSlugId()).not.toBe(randomSlugId());
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

it('a taken slug is a 409 about the slug, not any conflict', () => {
  expect(isSlugTaken(new ApiError(409, 'slug already in use.'))).toBe(true);
  expect(isSlugTaken(new ApiError(409, 'Slug already exists for this content type.'))).toBe(true);
  expect(isSlugTaken(new ApiError(409, 'This plugin allows only a single instance.'))).toBe(false);
  expect(isSlugTaken(new ApiError(400, "slug 'forms' is reserved"))).toBe(false);
});
