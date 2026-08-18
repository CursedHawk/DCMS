import { bodyNodes, parseCss, parseHtml, type ParsedCssRule, type ParsedNode } from '@dcms/gjs-parse';

/**
 * The synchronous parsers GrapesJS falls back to.
 *
 * The builder hands GrapesJS content that a web worker already parsed, via a
 * memo keyed on the exact input string. But GrapesJS also parses on its own
 * account, and those calls never pass through the worker: **every dropped
 * block**, every paste, every `append()` of a markup string, every RTE commit.
 *
 * The first version of the parser hook returned an empty array on a memo miss.
 * The result was that drag and drop silently did nothing at all — the drop
 * completed, the sorter ran, and the block parsed to zero components, with no
 * error in the console and no failing test, because every test preloaded the
 * memo first.
 *
 * So a miss parses synchronously here instead, with the same parser and the same
 * options the worker uses. That matters beyond just working: a dropped block and
 * the same markup loaded from a file must produce identical component trees, or
 * the round-trip guarantee holds for one and not the other. These inputs are a
 * block snippet, not a page, so the main-thread cost is negligible.
 */

/** Parser options shared by the worker and the fallback. Keep them in lockstep. */
export const PARSE_OPTIONS = { sanitize: true, allowScripts: false } as const;

export function parseHtmlSync(input: string): ParsedNode[] {
  // htmlparser2 implies html/body around a fragment; `setComponents` wants the
  // fragment's own nodes.
  return bodyNodes(parseHtml(input, PARSE_OPTIONS));
}

export function parseCssSync(input: string): ParsedCssRule[] {
  return parseCss(input);
}
