import { describe, expect, it } from 'vitest';
import { emptySiteManifest } from './site';
import { renderThemeCss, themeVarName, themeVariables } from './theme';

const base = emptySiteManifest().theme;

describe('themeVarName', () => {
  it('namespaces each group', () => {
    expect(themeVarName('color', 'brand')).toBe('--dcms-color-brand');
    expect(themeVarName('font', 'body')).toBe('--dcms-font-body');
    expect(themeVarName('space', 'lg')).toBe('--dcms-space-lg');
  });

  it('sanitizes a token name into a valid ident', () => {
    expect(themeVarName('color', 'Brand Primary!')).toBe('--dcms-color-Brand-Primary');
  });
});

describe('renderThemeCss', () => {
  it('emits an empty root block for an empty theme', () => {
    expect(renderThemeCss(base)).toContain(':root {\n}');
  });

  it('emits every token group plus radius', () => {
    const css = renderThemeCss({
      ...base,
      colors: { brand: '#0af' },
      fonts: { body: 'Inter, sans-serif' },
      spacing: { lg: '2rem' },
      text: { xl: '1.5rem' },
      shadows: { md: '0 4px 12px rgb(0 0 0 / 8%)' },
      metrics: { container: '72rem', 'tracking-heading': '-0.02em' },
      radius: '8px',
    });
    expect(css).toContain('--dcms-color-brand: #0af;');
    expect(css).toContain('--dcms-font-body: Inter, sans-serif;');
    expect(css).toContain('--dcms-space-lg: 2rem;');
    expect(css).toContain('--dcms-text-xl: 1.5rem;');
    expect(css).toContain('--dcms-shadow-md: 0 4px 12px rgb(0 0 0 / 8%);');
    expect(css).toContain('--dcms-radius: 8px;');
  });

  it('emits metrics without a group infix', () => {
    const css = renderThemeCss({ ...base, metrics: { container: '72rem', 'radius-lg': '1rem' } });
    expect(css).toContain('--dcms-container: 72rem;');
    expect(css).toContain('--dcms-radius-lg: 1rem;');
    // The infixed form would be a different variable, and every rule in the
    // block stylesheet reads the short one.
    expect(css).not.toContain('--dcms-metric-');
  });

  it('passes custom properties through, adding the -- prefix when missing', () => {
    const css = renderThemeCss({ ...base, custom: { '--raw': '1', shadow: '0 0 2px #000' } });
    expect(css).toContain('--raw: 1;');
    expect(css).toContain('--shadow: 0 0 2px #000;');
  });

  it('strips characters that would let a value escape its declaration', () => {
    const css = renderThemeCss({ ...base, colors: { brand: 'red; } body { display:none' } });
    expect(css).toContain('--dcms-color-brand: red body display:none;');
    expect(css.match(/}/g)).toHaveLength(1);
  });

  it('carries a header explaining the file is generated', () => {
    expect(renderThemeCss(base).startsWith('/* Generated from site.json')).toBe(true);
  });
});

describe('themeVariables', () => {
  it('lists every variable for the completion provider', () => {
    const vars = themeVariables({ ...base, colors: { brand: '#0af' }, radius: '8px' });
    expect(vars).toEqual([
      { name: '--dcms-color-brand', value: '#0af' },
      { name: '--dcms-radius', value: '8px' },
    ]);
  });
});
