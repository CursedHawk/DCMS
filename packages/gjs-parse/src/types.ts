/**
 * Structural mirrors of the GrapesJS parser contracts
 * (grapesjs/packages/core/src/parser/types.ts). They are re-declared here rather
 * than imported so this package stays free of GrapesJS — which matters because
 * everything in it runs inside a web worker, where GrapesJS (Backbone + DOM)
 * cannot go.
 *
 * The two contracts are what make the offload possible at all: GrapesJS accepts
 * a custom code parser returning plain `ParsedNode[]` (no DOM nodes) and a custom
 * CSS parser returning plain `ParsedCssRule[]`, so the expensive, string-heavy
 * half of loading a page can happen off the main thread and only the cheap
 * definition walk stays on it.
 */

export enum ParsedNodeType {
  element = 1,
  text = 3,
  comment = 8,
  document = 9,
  fragment = 11,
}

export const HTML_NAMESPACE = 'http://www.w3.org/1999/xhtml';

export interface ParsedNode {
  nodeType?: number;
  tagName?: string;
  namespaceURI?: string;
  attributes?: Record<string, string>;
  childNodes?: ParsedNode[];
  textContent?: string;
  /** Attributes present with an empty value that are booleans (`disabled`). */
  __boolAttributes?: string[];
  /** Doctype string, on a document node. */
  __doctype?: string;
  /** Written as `<img />` rather than `<img>`. */
  __selfClosing?: boolean;
}

/** One rule as a custom `parserCss` returns it; GrapesJS splits the selector itself. */
export interface ParsedCssRule {
  /** Full selector text, e.g. `.a, .b:hover`. */
  selectors: string;
  /** Flat declaration map; `!important` is kept in the value. */
  style: Record<string, string>;
  /** At-rule name without the `@`, e.g. `media`, `supports`, `keyframes`. */
  atRule?: string;
  /** At-rule condition, e.g. `(max-width: 640px)`. */
  params?: string;
}

export interface ParsedDocument {
  nodes: ParsedNode[];
  /** Doctype found in the input, if the input was a whole document. */
  doctype?: string;
  /** Rules lifted out of inline `<style>` elements. */
  css: ParsedCssRule[];
}
