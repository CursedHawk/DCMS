import { describe, expect, it } from 'vitest';
import { parseCss, symbolsIn } from './css';

describe('parseCss', () => {
  it('returns the selector text unsplit — GrapesJS splits it itself', () => {
    expect(parseCss('.a, .b:hover { color: red; }')).toEqual([
      { selectors: '.a,.b:hover', style: { color: 'red' } },
    ]);
  });

  it('lowercases property names and keeps values intact', () => {
    expect(parseCss('.a { COLOR: RED; margin: 0 auto; }')[0].style).toEqual({
      color: 'RED',
      margin: '0 auto',
    });
  });

  it('preserves !important in the value', () => {
    expect(parseCss('.a { color: red !important; }')[0].style).toEqual({ color: 'red !important' });
  });

  it('flattens media queries onto their rules, keeping the condition as authored', () => {
    expect(parseCss('@media (max-width: 640px) { .a { display: none; } }')).toEqual([
      { selectors: '.a', style: { display: 'none' }, atRule: 'media', params: '(max-width: 640px)' },
    ]);
  });

  it('handles nested at-rules', () => {
    const rules = parseCss('@supports (display: grid) { @media print { .a { color: red } } }');
    expect(rules).toHaveLength(1);
    expect(rules[0].atRule).toBe('media');
    expect(rules[0].selectors).toBe('.a');
  });

  it('keeps font-face as a declaration at-rule', () => {
    const rules = parseCss("@font-face { font-family: 'X'; src: url(x.woff2); }");
    expect(rules[0].atRule).toBe('font-face');
    expect(rules[0].style['font-family']).toBe("'X'");
  });

  it('expands keyframes into one rule per stop', () => {
    const rules = parseCss('@keyframes spin { from { opacity: 0 } to { opacity: 1 } }');
    expect(rules).toHaveLength(2);
    expect(rules.map((r) => r.selectors)).toEqual(['from', 'to']);
    expect(rules.every((r) => r.atRule === 'keyframes' && r.params === 'spin')).toBe(true);
  });

  it('keeps custom properties', () => {
    expect(parseCss(':root { --brand: #0af; }')[0].style).toEqual({ '--brand': '#0af' });
  });

  it('drops empty rules that carry no styling information', () => {
    expect(parseCss('.a {}')).toEqual([]);
  });

  it('drops @import and @charset', () => {
    expect(parseCss('@charset "utf-8"; @import url(x.css); .a { color: red }')).toHaveLength(1);
  });

  it('returns nothing for empty input', () => {
    expect(parseCss('')).toEqual([]);
    expect(parseCss('   ')).toEqual([]);
  });

  it('recovers from a half-typed stylesheet instead of blanking the canvas', () => {
    // css-tree is error tolerant; whatever it can recover is returned, and a
    // total failure returns [] rather than throwing.
    expect(() => parseCss('.a { color: red; } .b { color:')).not.toThrow();
  });
});

describe('symbolsIn', () => {
  const names = (source: string) => symbolsIn(source).classes.map((c) => c.name);

  it('collects every class a stylesheet defines, deduped', () => {
    expect(names('.b .a { color: red } .a:hover, .c { color: blue }')).toEqual(['b', 'a', 'c']);
  });

  it('finds classes inside at-rules', () => {
    expect(names('@media print { .only-print { display: block } }')).toEqual(['only-print']);
  });

  it('records the line and column of the first definition', () => {
    const { classes } = symbolsIn('.a { color: red }\n\n.b { color: blue }\n.a { color: green }');
    expect(classes).toEqual([
      { name: 'a', line: 1, column: 1 },
      { name: 'b', line: 3, column: 1 },
    ]);
  });

  it('collects declared custom properties only, not used ones', () => {
    const { properties } = symbolsIn(':root { --a: 1; color: var(--b) }');
    expect(properties).toEqual([{ name: '--a', line: 1, column: 9 }]);
  });

  it('returns nothing for empty or broken input', () => {
    expect(symbolsIn('')).toEqual({ classes: [], properties: [] });
  });
});
