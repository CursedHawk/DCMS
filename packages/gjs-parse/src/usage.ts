import { Parser } from 'htmlparser2';

/**
 * Where markup *uses* each class, the mirror of `symbolsIn`'s "where CSS defines
 * it". Together they give the code view both directions: Ctrl+Click a class in
 * HTML to reach the rule, and "find all references" on a rule to reach every
 * page using it — which is the question you actually want answered before
 * changing or deleting a rule.
 *
 * The tokenizer is used directly rather than building a document: only attribute
 * positions matter, so there is no reason to allocate a tree.
 */

export interface ClassUsage {
  name: string;
  /** 1-based, for a Monaco range. */
  line: number;
  column: number;
}

export function classUsagesIn(html: string): ClassUsage[] {
  if (!html.trim()) return [];

  const usages: ClassUsage[] = [];
  const lineStarts = [0];
  for (let i = 0; i < html.length; i++) {
    if (html.charCodeAt(i) === 10) lineStarts.push(i + 1);
  }

  let searchFrom = 0;
  const parser = new Parser(
    {
      onattribute(name, value) {
        if (name !== 'class' || !value.trim()) return;
        // htmlparser2 reports the attribute's end offset, not the value's, so
        // each class is located by searching the source from the last hit — which
        // also keeps repeated class names on distinct positions.
        const tagEnd = parser.endIndex;
        for (const raw of value.split(/\s+/)) {
          if (!raw) continue;
          const at = locate(html, raw, searchFrom, tagEnd);
          if (at === -1) continue;
          searchFrom = at + raw.length;
          usages.push({ name: raw, ...position(lineStarts, at) });
        }
      },
      onopentag() {
        // Attribute callbacks fire before the tag closes; nothing to do here, but
        // the handler must exist for htmlparser2 to run the tokenizer eagerly.
      },
    },
    { lowerCaseAttributeNames: true, recognizeSelfClosing: true },
  );

  parser.write(html);
  parser.end();
  return usages;
}

/**
 * Find `name` as a whole word at or after `from`. The bound stops a miss inside
 * one tag from matching text much further down the document.
 */
function locate(html: string, name: string, from: number, bound: number): number {
  let index = html.indexOf(name, from);
  while (index !== -1 && index <= bound + name.length) {
    const before = index === 0 ? '' : html[index - 1]!;
    const after = html[index + name.length] ?? '';
    if (!/[\w-]/.test(before) && !/[\w-]/.test(after)) return index;
    index = html.indexOf(name, index + 1);
  }
  return -1;
}

function position(lineStarts: number[], offset: number): { line: number; column: number } {
  let low = 0;
  let high = lineStarts.length - 1;
  while (low < high) {
    const mid = (low + high + 1) >> 1;
    if (lineStarts[mid]! <= offset) low = mid;
    else high = mid - 1;
  }
  return { line: low + 1, column: offset - lineStarts[low]! + 1 };
}
