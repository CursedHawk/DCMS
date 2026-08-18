import { describe, expect, it } from 'vitest';
import { handleRequest } from './handle';
import { lintHtml, toMarkers, type LintContext } from './lint';

const context: LintContext = {
  components: [
    { type: 'BlogList', bindable: true },
    { type: 'BlogDetail', bindable: true },
    { type: 'ChatWidget' },
  ],
  instances: [
    { slug: 'blog', contentTypes: ['post', 'author'] },
    { slug: 'shop', contentTypes: ['product'] },
  ],
  knownClasses: ['hero', 'hero__title', 'card'],
};

const lint = (html: string, overrides: Partial<LintContext> = {}) =>
  lintHtml(html, { ...context, ...overrides });

const rules = (html: string, overrides: Partial<LintContext> = {}) =>
  lint(html, overrides).map((d) => d.rule);

/** The exact text a diagnostic underlines — the check that ranges are useful. */
const underlined = (html: string, index = 0) => {
  const d = lint(html)[index]!;
  return html.slice(d.start, d.end);
};

describe('lintHtml', () => {
  it('accepts an empty or whitespace-only document', () => {
    expect(lint('')).toEqual([]);
    expect(lint('   \n  ')).toEqual([]);
  });

  it('reports nothing for clean markup', () => {
    const html = [
      '<section class="hero">',
      '  <h1 class="hero__title">Hi</h1>',
      '  <img src="/a.png" alt="A logo">',
      '  <a href="/x" target="_blank" rel="noopener noreferrer">Out</a>',
      '</section>',
    ].join('\n');
    expect(lint(html)).toEqual([]);
  });

  describe('components', () => {
    it('flags an unknown component type', () => {
      expect(rules('<div data-dcms-component="Nope"></div>')).toEqual(['unknown-component']);
    });

    it('underlines the type, not the whole tag', () => {
      expect(underlined('<div data-dcms-component="Nope"></div>')).toBe('Nope');
    });

    it('accepts a known component that needs no binding', () => {
      expect(lint('<div data-dcms-component="ChatWidget"></div>')).toEqual([]);
    });

    it('nudges a bindable component that has no binding', () => {
      const found = lint('<div data-dcms-component="BlogList"></div>');
      expect(found).toHaveLength(1);
      expect(found[0]).toMatchObject({ rule: 'unbound-component', severity: 'info' });
    });

    it('does not nudge a bindable component that has one', () => {
      const html =
        '<div data-dcms-component="BlogList" ' +
        `data-dcms-bindings='[{"propPath":"items","instanceSlug":"blog","query":{"contentType":"post"}}]'></div>`;
      expect(lint(html)).toEqual([]);
    });

    it('ignores plain markup with no component attribute', () => {
      expect(lint('<div data-dcms-props=\'{{{\'></div>')).toEqual([]);
    });
  });

  describe('placeholder JSON', () => {
    const wrap = (attrs: string) => `<div data-dcms-component="ChatWidget" ${attrs}></div>`;

    it('flags unparseable props', () => {
      expect(rules(wrap(`data-dcms-props='{oops}'`))).toEqual(['malformed-json']);
    });

    it('flags props that are not an object', () => {
      expect(rules(wrap(`data-dcms-props='[1,2]'`))).toEqual(['malformed-json']);
      expect(rules(wrap(`data-dcms-props='"text"'`))).toEqual(['malformed-json']);
    });

    it('flags bindings that are not an array', () => {
      expect(rules(wrap(`data-dcms-bindings='{"propPath":"items"}'`))).toEqual(['malformed-json']);
    });

    it('accepts well-formed props', () => {
      expect(lint(wrap(`data-dcms-props='{"heading":"Latest"}'`))).toEqual([]);
    });
  });

  describe('bindings', () => {
    const bind = (json: string) =>
      `<div data-dcms-component="BlogList" data-dcms-bindings='${json}'></div>`;

    it('flags a binding with no instance', () => {
      expect(rules(bind('[{"propPath":"items"}]'))).toEqual(['binding-no-instance']);
    });

    it('flags an instance this tenant does not have', () => {
      const found = lint(bind('[{"propPath":"items","instanceSlug":"ghost"}]'));
      expect(found.map((d) => d.rule)).toEqual(['unknown-instance']);
      expect(found[0]!.message).toContain('ghost');
    });

    it('warns when no content type is named', () => {
      const found = lint(bind('[{"propPath":"items","instanceSlug":"blog"}]'));
      expect(found).toHaveLength(1);
      expect(found[0]).toMatchObject({ rule: 'binding-no-content-type', severity: 'warning' });
    });

    it('flags a content type the instance does not offer', () => {
      const found = lint(
        bind('[{"propPath":"items","instanceSlug":"blog","query":{"contentType":"product"}}]'),
      );
      expect(found.map((d) => d.rule)).toEqual(['unknown-content-type']);
      // The message lists what is available, so the fix needs no doc lookup.
      expect(found[0]!.message).toContain('post, author');
    });

    it('accepts a content type the instance does offer', () => {
      expect(
        lint(bind('[{"propPath":"items","instanceSlug":"shop","query":{"contentType":"product"}}]')),
      ).toEqual([]);
    });

    it('checks every binding in the array', () => {
      expect(
        rules(
          bind(
            '[{"instanceSlug":"ghost"},' +
              '{"instanceSlug":"blog","query":{"contentType":"nope"}}]',
          ),
        ),
      ).toEqual(['unknown-instance', 'unknown-content-type']);
    });

    it('skips non-object entries rather than throwing', () => {
      expect(lint(bind('[null,7,"x"]'))).toEqual([]);
    });
  });

  describe('classes', () => {
    it('flags a class no stylesheet defines', () => {
      const found = lint('<div class="hero mystery"></div>');
      expect(found.map((d) => d.rule)).toEqual(['undefined-class']);
      expect(found[0]!.message).toContain('.mystery');
    });

    it('underlines only the offending class', () => {
      expect(underlined('<div class="hero mystery"></div>')).toBe('mystery');
    });

    it('honours ignored prefixes', () => {
      expect(lint('<div class="tw-grid"></div>', { ignoreClassPrefixes: ['tw-'] })).toEqual([]);
    });

    it('ignores empty and repeated whitespace', () => {
      expect(lint('<div class="  hero   card  "></div>')).toEqual([]);
    });
  });

  describe('ids and accessibility', () => {
    it('flags a duplicated id once, on the second use', () => {
      const found = lint('<div id="x"></div>\n<div id="x"></div>\n<div id="x"></div>');
      expect(found.map((d) => d.rule)).toEqual(['duplicate-id', 'duplicate-id']);
      expect(found[0]!.start).toBeGreaterThan(10);
    });

    it('allows distinct ids', () => {
      expect(lint('<div id="a"></div><div id="b"></div>')).toEqual([]);
    });

    it('flags an image with no alt attribute', () => {
      expect(rules('<img src="/a.png">')).toEqual(['img-no-alt']);
    });

    it('accepts an explicitly decorative image', () => {
      expect(lint('<img src="/a.png" alt="">')).toEqual([]);
    });

    it('flags a new-tab link with no noopener', () => {
      expect(rules('<a href="/x" target="_blank">Out</a>')).toEqual(['blank-no-noopener']);
    });

    it('accepts noopener inside a longer rel', () => {
      expect(lint('<a href="/x" target="_blank" rel="nofollow noopener">Out</a>')).toEqual([]);
    });
  });

  it('descends into nested elements', () => {
    const html = '<section class="hero"><div><span class="ghost">x</span></div></section>';
    expect(rules(html)).toEqual(['undefined-class']);
  });

  it('returns diagnostics in document order', () => {
    const html = ['<img src="/a.png">', '<div class="ghost"></div>', '<img src="/b.png">'].join(
      '\n',
    );
    const found = lint(html);
    expect(found.map((d) => d.rule)).toEqual(['img-no-alt', 'undefined-class', 'img-no-alt']);
    expect(found[0]!.start).toBeLessThan(found[1]!.start);
    expect(found[1]!.start).toBeLessThan(found[2]!.start);
  });

  it('survives malformed markup without throwing', () => {
    expect(() => lint('<div class="ghost"><span>oops')).not.toThrow();
  });
});

describe('toMarkers', () => {
  const source = 'line one\nline two\nline three';

  it('returns nothing for no diagnostics', () => {
    expect(toMarkers(source, [])).toEqual([]);
  });

  it('resolves offsets to 1-based line and column', () => {
    const [marker] = toMarkers(source, [
      { severity: 'error', rule: 'r', message: 'm', start: 9, end: 13 },
    ]);
    expect(marker).toMatchObject({ startLine: 2, startColumn: 1, endLine: 2, endColumn: 5 });
  });

  it('places an offset on the first line at column offset+1', () => {
    const [marker] = toMarkers(source, [
      { severity: 'error', rule: 'r', message: 'm', start: 0, end: 4 },
    ]);
    expect(marker).toMatchObject({ startLine: 1, startColumn: 1, endColumn: 5 });
  });

  it('spans lines when the range does', () => {
    const [marker] = toMarkers(source, [
      { severity: 'error', rule: 'r', message: 'm', start: 5, end: 20 },
    ]);
    expect(marker).toMatchObject({ startLine: 1, endLine: 3 });
  });

  it('clamps an out-of-range offset instead of producing NaN', () => {
    const [marker] = toMarkers(source, [
      { severity: 'error', rule: 'r', message: 'm', start: -5, end: 9999 },
    ]);
    expect(marker!.startLine).toBe(1);
    expect(marker!.startColumn).toBe(1);
    expect(marker!.endLine).toBe(3);
  });

  it('keeps the fields the diagnostic already carried', () => {
    const [marker] = toMarkers(source, [
      { severity: 'warning', rule: 'undefined-class', message: 'nope', start: 0, end: 1 },
    ]);
    expect(marker).toMatchObject({ severity: 'warning', rule: 'undefined-class', message: 'nope' });
  });
});

describe('lint over the worker protocol', () => {
  it('answers a lint request with positioned markers', () => {
    const response = handleRequest({
      kind: 'lint',
      id: 4,
      key: 'pages/home.html',
      source: '<div>\n  <img src="/a.png">\n</div>',
      context,
    });
    expect(response).toMatchObject({ kind: 'lint', id: 4, key: 'pages/home.html' });
    const diagnostics = (response as { diagnostics: { rule: string; startLine: number }[] })
      .diagnostics;
    expect(diagnostics).toHaveLength(1);
    expect(diagnostics[0]).toMatchObject({ rule: 'img-no-alt', startLine: 2, startColumn: 3 });
  });

  it('indexes class definitions with the file and line that define them', () => {
    const response = handleRequest({
      kind: 'index',
      id: 5,
      key: 'project',
      stylesheets: {
        'styles/global.css': '.hero { color: red }\n\n.card { color: blue }',
        // A later override must not steal go-to-definition from the base rule.
        'styles/pages/home.css': '.hero { color: green }\n:root { --dcms-gap: 8px }',
      },
    });
    expect(response).toMatchObject({
      kind: 'index',
      classNames: ['card', 'hero'],
      customProperties: ['--dcms-gap'],
      classSources: {
        hero: { path: 'styles/global.css', line: 1 },
        card: { path: 'styles/global.css', line: 3 },
      },
      propertySources: {
        '--dcms-gap': { path: 'styles/pages/home.css', line: 2 },
      },
    });
  });
});
