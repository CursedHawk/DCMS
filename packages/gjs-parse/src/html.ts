import { parseDocument } from 'htmlparser2';
import type { AnyNode, Element } from 'domhandler';
import { HTML_NAMESPACE, ParsedNodeType, type ParsedCssRule, type ParsedDocument, type ParsedNode } from './types';
import { parseCss } from './css';

/**
 * DOM-free HTML parsing. `htmlparser2` produces a plain object tree, which we map
 * onto the `ParsedNode` shape GrapesJS's parser accepts from a custom code parser
 * — so this whole function runs in a worker, and the main thread only walks the
 * (already tokenized) tree into component definitions.
 *
 * The mapping deliberately mirrors what GrapesJS's own DOM path produces
 * (parser/model/utils.ts `domToParsedNode`), including the `__boolAttributes` and
 * `__selfClosing` hints, so a page parsed here and a page parsed by the browser
 * fallback yield the same components.
 */

/** Attributes that are booleans in HTML: present-and-empty means `true`. */
const BOOLEAN_ATTRS = new Set([
  'allowfullscreen', 'async', 'autofocus', 'autoplay', 'checked', 'controls',
  'default', 'defer', 'disabled', 'formnovalidate', 'hidden', 'inert', 'ismap',
  'itemscope', 'loop', 'multiple', 'muted', 'nomodule', 'novalidate', 'open',
  'playsinline', 'readonly', 'required', 'reversed', 'selected',
]);

/** Elements with no closing tag; htmlparser2 already treats these as empty. */
const VOID_ELEMENTS = new Set([
  'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta',
  'param', 'source', 'track', 'wbr',
]);

export interface ParseHtmlOptions {
  /**
   * Drop `on*` handlers and `javascript:` values. GrapesJS sanitizes again on its
   * side, but doing it here keeps them out of the parsed tree entirely.
   * @default true
   */
  sanitize?: boolean;
  /**
   * Keep `<script>` elements. Off by default — the builder's Custom Code block is
   * the deliberate way to add script, so anything else is likely paste-in noise.
   * @default false
   */
  allowScripts?: boolean;
  /**
   * Keep whitespace-only text nodes. Off by default so re-indenting a file in the
   * code view does not produce a tree of empty text components.
   * @default false
   */
  keepEmptyTextNodes?: boolean;
  /** Lift rules out of inline `<style>` elements into `css`. @default true */
  extractStyles?: boolean;
}

export function parseHtml(input: string, options: ParseHtmlOptions = {}): ParsedDocument {
  const {
    sanitize = true,
    allowScripts = false,
    keepEmptyTextNodes = false,
    extractStyles = true,
  } = options;

  const doc = parseDocument(input, {
    lowerCaseTags: true,
    lowerCaseAttributeNames: true,
    recognizeSelfClosing: true,
    decodeEntities: true,
  });

  const css: ParsedCssRule[] = [];
  let doctype: string | undefined;
  const nodes: ParsedNode[] = [];

  for (const child of doc.children) {
    if (isDirective(child)) {
      // `<!doctype html>` arrives as a directive named "!doctype".
      if (child.name?.toLowerCase() === '!doctype') doctype = `<${child.data}>`;
      continue;
    }
    const mapped = mapNode(child, { sanitize, allowScripts, keepEmptyTextNodes, extractStyles }, css);
    if (mapped) nodes.push(mapped);
  }

  return { nodes, doctype, css };
}

type Ctx = Required<ParseHtmlOptions>;

interface DirectiveLike {
  type: string;
  name?: string;
  data?: string;
}

function isDirective(node: AnyNode): node is AnyNode & DirectiveLike {
  return node.type === 'directive';
}

function mapNode(node: AnyNode, ctx: Ctx, css: ParsedCssRule[]): ParsedNode | null {
  switch (node.type) {
    case 'text': {
      const text = (node as unknown as { data: string }).data;
      if (!ctx.keepEmptyTextNodes && text.trim() === '') return null;
      return { nodeType: ParsedNodeType.text, textContent: text };
    }
    case 'comment':
      return {
        nodeType: ParsedNodeType.comment,
        textContent: (node as unknown as { data: string }).data,
      };
    case 'tag':
    case 'script':
    case 'style':
      return mapElement(node as Element, ctx, css);
    default:
      // CDATA and processing instructions have no component representation.
      return null;
  }
}

function mapElement(el: Element, ctx: Ctx, css: ParsedCssRule[]): ParsedNode | null {
  const tagName = el.name.toLowerCase();

  if (tagName === 'script' && !ctx.allowScripts) return null;

  // An inline stylesheet becomes real CSS rules rather than a component, so its
  // declarations land in the Style Manager instead of being frozen as markup.
  if (tagName === 'style' && ctx.extractStyles) {
    const text = textOf(el);
    if (text.trim()) css.push(...parseCss(text));
    return null;
  }

  const attributes: Record<string, string> = {};
  const boolAttributes: string[] = [];
  for (const [name, rawValue] of Object.entries(el.attribs ?? {})) {
    const value = rawValue ?? '';
    if (ctx.sanitize && isUnsafeAttribute(name, value)) continue;
    attributes[name] = value;
    if (value === '' && BOOLEAN_ATTRS.has(name)) boolAttributes.push(name);
  }

  const childNodes: ParsedNode[] = [];
  for (const child of el.children) {
    const mapped = mapNode(child, ctx, css);
    if (mapped) childNodes.push(mapped);
  }

  const parsed: ParsedNode = {
    nodeType: ParsedNodeType.element,
    tagName,
    namespaceURI: HTML_NAMESPACE,
  };
  if (Object.keys(attributes).length) parsed.attributes = attributes;
  if (boolAttributes.length) parsed.__boolAttributes = boolAttributes;
  if (childNodes.length) parsed.childNodes = childNodes;
  if (VOID_ELEMENTS.has(tagName)) parsed.__selfClosing = true;

  return parsed;
}

function isUnsafeAttribute(name: string, value: string): boolean {
  return name.startsWith('on') || value.trimStart().toLowerCase().startsWith('javascript:');
}

function textOf(el: Element): string {
  return el.children
    .map((c) => (c.type === 'text' ? (c as unknown as { data: string }).data : ''))
    .join('');
}

/**
 * The body-level nodes of a parsed document. A page file normally holds a
 * fragment, but a pasted or hand-written file may be a whole document; in that
 * case the builder should edit its body, not render `<html>` as a component.
 */
export function bodyNodes(parsed: ParsedDocument): ParsedNode[] {
  const html = parsed.nodes.find((n) => n.tagName === 'html');
  if (!html) return parsed.nodes;
  const body = (html.childNodes ?? []).find((n) => n.tagName === 'body');
  return body?.childNodes ?? [];
}

/** True when the input looks like a whole document rather than a fragment. */
export function isDocument(parsed: ParsedDocument): boolean {
  return parsed.doctype !== undefined || parsed.nodes.some((n) => n.tagName === 'html');
}
