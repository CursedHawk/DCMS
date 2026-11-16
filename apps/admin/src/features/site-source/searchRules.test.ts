import { describe, expect, it } from 'vitest';
import { hiddenBy, matches, pathProblem, setPageShown, type SearchSettings } from './searchRules';

const base: SearchSettings = { indexable: true, exclude: [], include: [] };

describe('search rules', () => {
  it.each([
    ['/about', '/About', true],
    ['/about', '/about/team', false],
    ['/blog/:slug', '/blog/hello', true],
    ['/blog/:slug', '/blog/:slug', true],
    ['/private/*', '/private', true],
    ['/private/*', '/private/a/b', true],
    ['/private/*', '/privateer', false],
  ])('%s covers %s: %s', (rule, path, covered) => {
    expect(matches(rule, path)).toBe(covered);
  });

  it('switching a page off adds its own rule once, and on removes only that rule', () => {
    const off = setPageShown(setPageShown(base, '/about', false), '/About', false);
    expect(off.exclude).toEqual(['/about']);
    const sectioned = { ...off, exclude: [...off.exclude, '/*'] };
    const on = setPageShown(sectioned, '/about', true);
    expect(on.exclude).toEqual(['/*']);
    expect(hiddenBy(on, '/about')).toBe('/*');
    expect(hiddenBy(sectioned, '/about')).toBe('/about');
  });

  it('checks a typed path the way the server will', () => {
    expect(pathProblem('/private/*', true)).toBeNull();
    expect(pathProblem('/blog/:slug', true)).toBeNull();
    expect(pathProblem('about', true)).toBe('start');
    expect(pathProblem('/a b', true)).toBe('chars');
    expect(pathProblem('/a/../b', true)).toBe('chars');
    expect(pathProblem('/a/*/b', true)).toBe('pattern');
    expect(pathProblem('/blog/:slug', false)).toBe('pattern');
    expect(pathProblem('/landing', false)).toBeNull();
  });
});
