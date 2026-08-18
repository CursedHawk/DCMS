import * as csstree from 'css-tree';
import type { ParsedCssRule } from './types';

/**
 * DOM-free CSS parsing. The browser path GrapesJS uses by default builds a real
 * `<style>` element and reads `CSSStyleSheet`, which a worker cannot do; css-tree
 * gives the same information from a string.
 *
 * The output is the `ParsedCssRule` shape a custom `parserCss` returns: the whole
 * selector text as a string plus a flat declaration map. GrapesJS's `ParserCss`
 * splits the selector into class sets, extracts `:hover`-style states and moves
 * non-class selectors into `selectorsAdd` — so none of that is duplicated here.
 */

/** At-rules that wrap other rules rather than holding declarations directly. */
const NESTING_AT_RULES = new Set(['media', 'supports', 'document', 'container', 'layer', 'scope']);

/** At-rules whose block holds declarations for the at-rule itself. */
const DECLARATION_AT_RULES = new Set(['font-face', 'page', 'counter-style', 'viewport', 'property']);

export function parseCss(input: string): ParsedCssRule[] {
  if (!input.trim()) return [];

  let ast: csstree.CssNode;
  try {
    // Positions are not needed and roughly double parse cost on large sheets.
    ast = csstree.parse(input, { positions: false, parseValue: false, parseAtrulePrelude: false });
  } catch {
    // A half-typed stylesheet in the code view must not blank the canvas.
    return [];
  }

  const rules: ParsedCssRule[] = [];
  if (ast.type !== 'StyleSheet') return rules;
  collect(ast.children, rules, undefined, undefined);
  return rules;
}

function collect(
  children: csstree.List<csstree.CssNode>,
  out: ParsedCssRule[],
  atRule: string | undefined,
  params: string | undefined,
): void {
  children.forEach((node) => {
    if (node.type === 'Rule') {
      const selectors = generate(node.prelude);
      const style = declarations(node.block);
      // A rule with no declarations carries no information and would only add
      // noise to the Style Manager.
      if (selectors && Object.keys(style).length > 0) {
        out.push(trimUndefined({ selectors, style, atRule, params }));
      }
      return;
    }

    if (node.type === 'Atrule') {
      const name = node.name.toLowerCase();
      const prelude = node.prelude ? generate(node.prelude) : '';

      if (DECLARATION_AT_RULES.has(name)) {
        const style = node.block ? declarations(node.block) : {};
        if (Object.keys(style).length > 0) {
          out.push(trimUndefined({ selectors: prelude, style, atRule: name, params: prelude }));
        }
        return;
      }

      if (name === 'keyframes' || name.endsWith('keyframes')) {
        // Each keyframe selector (`0%`, `from`) is its own rule under the at-rule.
        if (node.block) collect(node.block.children, out, 'keyframes', prelude);
        return;
      }

      if (NESTING_AT_RULES.has(name) && node.block) {
        collect(node.block.children, out, name, prelude);
        return;
      }

      // @import / @charset / @namespace and anything unrecognized carry no
      // component styling; dropping them is what the browser parser does too.
    }
  });
}

function declarations(block: csstree.Block): Record<string, string> {
  const style: Record<string, string> = {};
  block.children.forEach((child) => {
    if (child.type !== 'Declaration') return;
    const value = generate(child.value);
    style[child.property.toLowerCase()] = child.important ? `${value} !important` : value;
  });
  return style;
}

function generate(node: csstree.CssNode): string {
  try {
    return csstree.generate(node).trim();
  } catch {
    return '';
  }
}

function trimUndefined(rule: ParsedCssRule): ParsedCssRule {
  const out: ParsedCssRule = { selectors: rule.selectors, style: rule.style };
  if (rule.atRule) out.atRule = rule.atRule;
  if (rule.params) out.params = rule.params;
  return out;
}

export interface SymbolDefinition {
  name: string;
  /** 1-based, matching what Monaco wants for a target range. */
  line: number;
  column: number;
}

export interface StylesheetSymbols {
  classes: SymbolDefinition[];
  /** Custom properties (`--x`), for `var()` completion and navigation. */
  properties: SymbolDefinition[];
}

/**
 * Where a stylesheet defines each class and custom property. This is the code
 * view's whole symbol source: it feeds `class="…"` and `var(--…)` completion (so
 * authors get the project's real names rather than guesses) and go-to-definition
 * from either one back to the rule that declares it.
 */
export function symbolsIn(input: string): StylesheetSymbols {
  const classes: SymbolDefinition[] = [];
  const properties: SymbolDefinition[] = [];
  // First definition wins: go-to-definition should land on the base rule, not on
  // whichever override happens to come last in the sheet.
  const seenClasses = new Set<string>();
  const seenProperties = new Set<string>();

  try {
    const ast = csstree.parse(input, { positions: true, parseValue: false });
    csstree.walk(ast, (node) => {
      if (node.type === 'ClassSelector' && !seenClasses.has(node.name)) {
        seenClasses.add(node.name);
        classes.push(at(node.name, node.loc));
      } else if (
        node.type === 'Declaration' &&
        node.property.startsWith('--') &&
        !seenProperties.has(node.property)
      ) {
        seenProperties.add(node.property);
        properties.push(at(node.property, node.loc));
      }
    });
  } catch {
    // Ignore: a broken stylesheet simply contributes no definitions.
  }
  return { classes, properties };
}

function at(name: string, loc: csstree.CssLocation | null | undefined): SymbolDefinition {
  return { name, line: loc?.start.line ?? 1, column: loc?.start.column ?? 1 };
}

