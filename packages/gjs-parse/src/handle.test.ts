import { describe, expect, it } from 'vitest';
import { formatCss, formatHtml } from './format';
import { handleRequest } from './handle';

describe('formatHtml', () => {
  it('indents a single-line serialization so git diffs stay readable', () => {
    const out = formatHtml('<section><h1>A</h1><p>B</p></section>');
    expect(out.split('\n').length).toBeGreaterThan(3);
    expect(out.endsWith('\n')).toBe(true);
  });

  it('is idempotent — formatting twice changes nothing', () => {
    const once = formatHtml('<div><span>a</span><span>b</span></div>');
    expect(formatHtml(once)).toBe(once);
  });

  it('leaves pre content alone', () => {
    const out = formatHtml('<pre>  keep\n   this  </pre>');
    expect(out).toContain('  keep\n   this  ');
  });

  it('returns empty for empty input', () => {
    expect(formatHtml('')).toBe('');
    expect(formatHtml('  ')).toBe('');
  });
});

describe('formatCss', () => {
  it('expands a minified stylesheet', () => {
    const out = formatCss('.a{color:red}.b{color:blue}');
    expect(out).toContain('.a {');
    expect(out.endsWith('\n')).toBe(true);
  });

  it('is idempotent', () => {
    const once = formatCss('.a{color:red}.b{color:blue}');
    expect(formatCss(once)).toBe(once);
  });
});

describe('handleRequest', () => {
  it('answers a parse-html request, echoing id and key', () => {
    const res = handleRequest({ kind: 'parse-html', id: 7, key: 'pages/home.html', input: '<p>a</p>' });
    expect(res.kind).toBe('parse-html');
    expect(res.id).toBe(7);
    expect(res.key).toBe('pages/home.html');
    expect(res.kind === 'parse-html' && res.result.nodes[0].tagName).toBe('p');
  });

  it('answers a parse-css request', () => {
    const res = handleRequest({ kind: 'parse-css', id: 1, key: 'g', input: '.a{color:red}' });
    expect(res.kind === 'parse-css' && res.result).toEqual([{ selectors: '.a', style: { color: 'red' } }]);
  });

  it('formats only the halves it was given', () => {
    const res = handleRequest({ kind: 'format', id: 2, key: 'k', html: '<p>a</p>' });
    expect(res.kind === 'format' && res.html).toBeTruthy();
    expect(res.kind === 'format' && res.css).toBeUndefined();
  });

  it('indexes classes and custom properties across the whole project', () => {
    const res = handleRequest({
      kind: 'index',
      id: 3,
      key: 'project',
      stylesheets: {
        'styles/theme.css': ':root { --dcms-color-brand: #0af }',
        'styles/global.css': '.hero { color: red }',
        'styles/pages/home.css': '.hero { padding: 1rem } .card { color: blue }',
      },
    });

    expect(res.kind).toBe('index');
    if (res.kind !== 'index') return;
    expect(res.classNames).toEqual(['card', 'hero']);
    expect(res.customProperties).toEqual(['--dcms-color-brand']);
    // First definition wins so go-to-definition lands on the base rule.
    expect(res.classSources.hero).toMatchObject({ path: 'styles/global.css', line: 1 });
    expect(res.classSources.card).toMatchObject({ path: 'styles/pages/home.css', line: 1 });
    expect(res.propertySources['--dcms-color-brand']).toMatchObject({ path: 'styles/theme.css' });
  });
});
