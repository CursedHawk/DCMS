import { describe, expect, it } from 'vitest';
import { handleRequest } from './handle';
import { classUsagesIn } from './usage';

const names = (html: string) => classUsagesIn(html).map((u) => u.name);

describe('classUsagesIn', () => {
  it('returns nothing for empty input', () => {
    expect(classUsagesIn('')).toEqual([]);
    expect(classUsagesIn('   ')).toEqual([]);
  });

  it('collects every class in an attribute', () => {
    expect(names('<div class="a b c"></div>')).toEqual(['a', 'b', 'c']);
  });

  it('collects classes from nested elements', () => {
    expect(names('<section class="hero"><h1 class="title">x</h1></section>')).toEqual([
      'hero',
      'title',
    ]);
  });

  it('keeps repeated uses as separate entries', () => {
    const usages = classUsagesIn('<div class="card"></div>\n<div class="card"></div>');
    expect(usages).toHaveLength(2);
    expect(usages[0]!.line).toBe(1);
    expect(usages[1]!.line).toBe(2);
  });

  it('records the column the class starts at', () => {
    // `<div class="hero">` — h of hero is the 13th character.
    expect(classUsagesIn('<div class="hero">')[0]).toEqual({ name: 'hero', line: 1, column: 13 });
  });

  it('locates a class on a later line', () => {
    const html = '<div>\n  <span class="badge">x</span>\n</div>';
    expect(classUsagesIn(html)[0]).toMatchObject({ name: 'badge', line: 2, column: 16 });
  });

  it('ignores an empty class attribute', () => {
    expect(classUsagesIn('<div class=""></div>')).toEqual([]);
    expect(classUsagesIn('<div class="   "></div>')).toEqual([]);
  });

  it('ignores other attributes that look like classes', () => {
    expect(names('<div id="a" data-x="b"></div>')).toEqual([]);
  });

  it('handles single-quoted and unquoted attributes', () => {
    expect(names("<div class='a b'></div>")).toEqual(['a', 'b']);
    expect(names('<div class=solo></div>')).toEqual(['solo']);
  });

  it('does not match a class name that is a prefix of a longer one', () => {
    // `hero` must land on the standalone use, not inside `hero-title`.
    const usages = classUsagesIn('<div class="hero-title"></div><div class="hero"></div>');
    expect(usages.map((u) => u.name)).toEqual(['hero-title', 'hero']);
    expect(usages[1]!.column).toBeGreaterThan(usages[0]!.column);
  });

  it('survives malformed markup', () => {
    expect(() => classUsagesIn('<div class="a"><span class="b">')).not.toThrow();
    expect(names('<div class="a"><span class="b">')).toEqual(['a', 'b']);
  });
});

describe('the index request, with pages', () => {
  const response = () =>
    handleRequest({
      kind: 'index',
      id: 1,
      key: 'project',
      stylesheets: { 'styles/global.css': '.hero { color: red }' },
      pages: {
        'pages/home.html': '<section class="hero ghost"></section>',
        'pages/about.html': '<div class="hero"></div>',
      },
    });

  it('lists every place each class is used', () => {
    const res = response();
    expect(res.kind).toBe('index');
    if (res.kind !== 'index') return;
    expect(res.classUsages.hero).toEqual([
      { path: 'pages/home.html', line: 1, column: 17 },
      { path: 'pages/about.html', line: 1, column: 13 },
    ]);
    expect(res.classUsages.ghost).toHaveLength(1);
  });

  it('keeps used-but-undefined classes out of the defined list', () => {
    const res = response();
    if (res.kind !== 'index') return;
    // Otherwise the "nothing defines this class" diagnostic could never fire.
    expect(res.classNames).toEqual(['hero']);
  });

  it('tolerates a request with no pages at all', () => {
    const res = handleRequest({
      kind: 'index',
      id: 2,
      key: 'project',
      stylesheets: { 'styles/global.css': '.hero { color: red }' },
    });
    if (res.kind !== 'index') return;
    expect(res.classUsages).toEqual({});
  });
});
