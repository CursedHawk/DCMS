import { describe, expect, it } from 'vitest';
import { bodyNodes, isDocument, parseHtml } from './html';
import { HTML_NAMESPACE, ParsedNodeType } from './types';

describe('parseHtml', () => {
  it('maps elements to the GrapesJS ParsedNode shape', () => {
    const { nodes } = parseHtml('<section class="hero" id="top"><h1>Hi</h1></section>');

    expect(nodes).toHaveLength(1);
    const section = nodes[0];
    expect(section.nodeType).toBe(ParsedNodeType.element);
    expect(section.tagName).toBe('section');
    expect(section.namespaceURI).toBe(HTML_NAMESPACE);
    expect(section.attributes).toEqual({ class: 'hero', id: 'top' });

    const h1 = section.childNodes?.[0];
    expect(h1?.tagName).toBe('h1');
    expect(h1?.childNodes?.[0]).toEqual({ nodeType: ParsedNodeType.text, textContent: 'Hi' });
  });

  it('lowercases tags and attribute names', () => {
    const { nodes } = parseHtml('<SECTION DATA-X="1"></SECTION>');
    expect(nodes[0].tagName).toBe('section');
    expect(nodes[0].attributes).toEqual({ 'data-x': '1' });
  });

  it('flags boolean attributes so they round-trip as booleans', () => {
    const { nodes } = parseHtml('<input disabled required value="a" />');
    expect(nodes[0].__boolAttributes).toEqual(['disabled', 'required']);
    expect(nodes[0].attributes).toEqual({ disabled: '', required: '', value: 'a' });
  });

  it('marks void elements as self-closing', () => {
    const { nodes } = parseHtml('<img src="a.png" /><br>');
    expect(nodes[0].__selfClosing).toBe(true);
    expect(nodes[1].__selfClosing).toBe(true);
  });

  it('does not mark a normal element as self-closing', () => {
    expect(parseHtml('<div></div>').nodes[0].__selfClosing).toBeUndefined();
  });

  it('keeps comments as comment nodes', () => {
    const { nodes } = parseHtml('<!-- note --><p>x</p>');
    expect(nodes[0]).toEqual({ nodeType: ParsedNodeType.comment, textContent: ' note ' });
  });

  it('drops whitespace-only text by default and keeps it on request', () => {
    expect(parseHtml('<div>\n  <p>a</p>\n</div>').nodes[0].childNodes).toHaveLength(1);
    expect(
      parseHtml('<div>\n  <p>a</p>\n</div>', { keepEmptyTextNodes: true }).nodes[0].childNodes,
    ).toHaveLength(3);
  });

  it('decodes entities in text', () => {
    const { nodes } = parseHtml('<p>a &amp; b &lt;c&gt;</p>');
    expect(nodes[0].childNodes?.[0].textContent).toBe('a & b <c>');
  });

  it('preserves the plugin placeholder attributes verbatim', () => {
    const html =
      '<div data-dcms-component="BlogList" data-dcms-props=\'{"heading":"Posts"}\' ' +
      'data-dcms-bindings=\'[{"propPath":"items","instanceSlug":"blog","query":{"contentType":"post"}}]\'></div>';
    const { nodes } = parseHtml(html);

    expect(nodes[0].attributes?.['data-dcms-component']).toBe('BlogList');
    expect(JSON.parse(nodes[0].attributes!['data-dcms-props'])).toEqual({ heading: 'Posts' });
    expect(JSON.parse(nodes[0].attributes!['data-dcms-bindings'])).toHaveLength(1);
  });
});

describe('parseHtml sanitization', () => {
  it('drops inline event handlers and javascript: values', () => {
    const { nodes } = parseHtml('<a href="javascript:alert(1)" onclick="x()" title="ok">a</a>');
    expect(nodes[0].attributes).toEqual({ title: 'ok' });
  });

  it('keeps them when sanitization is off', () => {
    const { nodes } = parseHtml('<a onclick="x()">a</a>', { sanitize: false });
    expect(nodes[0].attributes).toEqual({ onclick: 'x()' });
  });

  it('drops script elements by default and keeps them on request', () => {
    expect(parseHtml('<p>a</p><script>evil()</script>').nodes).toHaveLength(1);
    const kept = parseHtml('<script>ok()</script>', { allowScripts: true }).nodes;
    expect(kept[0].tagName).toBe('script');
  });
});

describe('parseHtml style extraction', () => {
  it('lifts inline stylesheets into css rules instead of components', () => {
    const { nodes, css } = parseHtml('<style>.a{color:red}</style><p>x</p>');
    expect(nodes).toHaveLength(1);
    expect(nodes[0].tagName).toBe('p');
    expect(css).toEqual([{ selectors: '.a', style: { color: 'red' } }]);
  });

  it('keeps the style element when extraction is off', () => {
    const { nodes, css } = parseHtml('<style>.a{color:red}</style>', { extractStyles: false });
    expect(nodes[0].tagName).toBe('style');
    expect(css).toEqual([]);
  });
});

describe('document handling', () => {
  const doc = `<!doctype html><html lang="en"><head><title>T</title></head><body><p>hi</p></body></html>`;

  it('detects a whole document and captures its doctype', () => {
    const parsed = parseHtml(doc);
    expect(isDocument(parsed)).toBe(true);
    expect(parsed.doctype?.toLowerCase()).toContain('doctype html');
  });

  it('exposes the body children so the builder edits the body, not <html>', () => {
    const body = bodyNodes(parseHtml(doc));
    expect(body).toHaveLength(1);
    expect(body[0].tagName).toBe('p');
  });

  it('treats a fragment as its own node list', () => {
    const parsed = parseHtml('<p>a</p><p>b</p>');
    expect(isDocument(parsed)).toBe(false);
    expect(bodyNodes(parsed)).toHaveLength(2);
  });
});

describe('malformed input', () => {
  it('recovers from unclosed tags rather than throwing', () => {
    const { nodes } = parseHtml('<div><p>unclosed');
    expect(nodes[0].tagName).toBe('div');
    expect(nodes[0].childNodes?.[0].tagName).toBe('p');
  });

  it('returns nothing for empty input', () => {
    expect(parseHtml('').nodes).toEqual([]);
    expect(parseHtml('   ').nodes).toEqual([]);
  });
});
