import { parseDocument } from 'htmlparser2';
import type { AnyNode, Element } from 'domhandler';

/**
 * Cross-file diagnostics for page markup.
 *
 * Monaco's own HTML worker validates syntax and knows the tags we taught it, but
 * it cannot see the rest of the project: whether a class is ever defined, or
 * whether `instanceSlug` names a plugin instance this tenant actually has. Those
 * checks need the whole project, which is exactly why they run here in a worker
 * rather than on the main thread.
 *
 * Every rule earns its place by catching something that silently produces a
 * broken published page — an unstyled class, a binding that fetches nothing, an
 * image with no alt text.
 */

export type LintSeverity = 'error' | 'warning' | 'info';

export interface LintDiagnostic {
  severity: LintSeverity;
  message: string;
  /** Zero-based offsets into the linted source. */
  start: number;
  end: number;
  /** Stable rule id, for suppression and tests. */
  rule: string;
}

export interface LintInstance {
  slug: string;
  contentTypes: string[];
}

export interface LintComponent {
  type: string;
  /**
   * Whether this component reads tenant content. Only these are worth nagging
   * about a missing binding — a chat widget or a search box renders fine without
   * one, and a warning there would just teach authors to ignore the squiggles.
   */
  bindable?: boolean;
}

export interface LintContext {
  /** Valid `data-dcms-component` values for this tenant. */
  components: readonly LintComponent[];
  /** The tenant's enabled plugin instances. */
  instances: readonly LintInstance[];
  /** Every class defined anywhere in the project's CSS. */
  knownClasses: readonly string[];
  /**
   * Class prefixes that are never expected to be defined locally (utility
   * frameworks, third-party embeds). Without this, pasting in Tailwind markup
   * would produce hundreds of "undefined class" warnings.
   */
  ignoreClassPrefixes?: readonly string[];
}

const COMPONENT_ATTR = 'data-dcms-component';
const PROPS_ATTR = 'data-dcms-props';
const BINDINGS_ATTR = 'data-dcms-bindings';

export function lintHtml(source: string, context: LintContext): LintDiagnostic[] {
  if (!source.trim()) return [];

  const diagnostics: LintDiagnostic[] = [];
  const doc = parseDocument(source, {
    lowerCaseTags: true,
    lowerCaseAttributeNames: true,
    recognizeSelfClosing: true,
    withStartIndices: true,
    withEndIndices: true,
  });

  const knownClasses = new Set(context.knownClasses);
  const components = new Map(context.components.map((c) => [c.type, c]));
  const instances = new Map(context.instances.map((i) => [i.slug, i]));
  const ignorePrefixes = context.ignoreClassPrefixes ?? [];
  const seenIds = new Map<string, number>();

  walk(doc.children, (el) => {
    const range = tagRange(el, source);
    const attrs = el.attribs ?? {};

    checkPlaceholder(attrs, range, source, { components, instances }, diagnostics);
    checkClasses(attrs, range, source, knownClasses, ignorePrefixes, diagnostics);
    checkDuplicateId(attrs, range, seenIds, diagnostics);
    checkAccessibility(el, attrs, range, diagnostics);
  });

  return diagnostics.sort((a, b) => a.start - b.start);
}

function walk(nodes: AnyNode[], visit: (el: Element) => void): void {
  for (const node of nodes) {
    if (node.type === 'tag' || node.type === 'script' || node.type === 'style') {
      const el = node as Element;
      visit(el);
      walk(el.children, visit);
    }
  }
}

/** The element's opening tag, so a squiggle covers `<div …>` and not its content. */
function tagRange(el: Element, source: string): { start: number; end: number } {
  const start = el.startIndex ?? 0;
  const close = source.indexOf('>', start);
  return { start, end: close === -1 ? (el.endIndex ?? start) + 1 : close + 1 };
}

/** Locate `needle` inside the element's opening tag, for a precise range. */
function rangeOf(
  source: string,
  tag: { start: number; end: number },
  needle: string,
): { start: number; end: number } {
  const index = source.indexOf(needle, tag.start);
  return index === -1 || index >= tag.end
    ? tag
    : { start: index, end: index + needle.length };
}

function checkPlaceholder(
  attrs: Record<string, string>,
  tag: { start: number; end: number },
  source: string,
  known: { components: Map<string, LintComponent>; instances: Map<string, LintInstance> },
  out: LintDiagnostic[],
): void {
  const type = attrs[COMPONENT_ATTR];
  if (type === undefined) return;

  const spec = known.components.get(type);
  if (!spec) {
    out.push({
      severity: 'error',
      rule: 'unknown-component',
      message:
        `"${type}" is not a component this site can use. ` +
        'It will publish as an empty element. Check the spelling, or enable the plugin that provides it.',
      ...rangeOf(source, tag, type),
    });
  }

  for (const [attr, expectArray] of [
    [PROPS_ATTR, false],
    [BINDINGS_ATTR, true],
  ] as const) {
    const raw = attrs[attr];
    if (raw === undefined) continue;
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch (e) {
      out.push({
        severity: 'error',
        rule: 'malformed-json',
        message: `${attr} is not valid JSON: ${(e as Error).message}`,
        ...rangeOf(source, tag, attr),
      });
      continue;
    }
    if (expectArray && !Array.isArray(parsed)) {
      out.push({
        severity: 'error',
        rule: 'malformed-json',
        message: `${attr} must be a JSON array of bindings.`,
        ...rangeOf(source, tag, attr),
      });
      continue;
    }
    if (!expectArray && (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed))) {
      out.push({
        severity: 'error',
        rule: 'malformed-json',
        message: `${attr} must be a JSON object.`,
        ...rangeOf(source, tag, attr),
      });
      continue;
    }
    if (expectArray) checkBindings(parsed as unknown[], tag, source, known.instances, out);
  }

  if (spec?.bindable && attrs[BINDINGS_ATTR] === undefined) {
    out.push({
      severity: 'info',
      rule: 'unbound-component',
      message: `"${type}" has no data binding yet, so it will render empty. Choose a content source in the settings panel.`,
      ...rangeOf(source, tag, COMPONENT_ATTR),
    });
  }
}

function checkBindings(
  bindings: unknown[],
  tag: { start: number; end: number },
  source: string,
  instances: Map<string, LintInstance>,
  out: LintDiagnostic[],
): void {
  for (const raw of bindings) {
    if (!raw || typeof raw !== 'object') continue;
    const binding = raw as { instanceSlug?: unknown; query?: { contentType?: unknown } };
    const slug = typeof binding.instanceSlug === 'string' ? binding.instanceSlug : null;

    if (!slug) {
      out.push({
        severity: 'error',
        rule: 'binding-no-instance',
        message: 'This binding has no instanceSlug, so it will fetch nothing.',
        ...rangeOf(source, tag, BINDINGS_ATTR),
      });
      continue;
    }

    const instance = instances.get(slug);
    if (!instance) {
      out.push({
        severity: 'error',
        rule: 'unknown-instance',
        message: `No enabled plugin instance is called "${slug}". The published page will show nothing here.`,
        ...rangeOf(source, tag, slug),
      });
      continue;
    }

    const contentType = binding.query?.contentType;
    if (typeof contentType !== 'string' || !contentType) {
      out.push({
        severity: 'warning',
        rule: 'binding-no-content-type',
        message: `This binding does not say which content type to fetch from "${slug}".`,
        ...rangeOf(source, tag, slug),
      });
    } else if (!instance.contentTypes.includes(contentType)) {
      out.push({
        severity: 'error',
        rule: 'unknown-content-type',
        message:
          `"${slug}" does not provide the content type "${contentType}". ` +
          `It offers: ${instance.contentTypes.join(', ') || 'none'}.`,
        ...rangeOf(source, tag, contentType),
      });
    }
  }
}

function checkClasses(
  attrs: Record<string, string>,
  tag: { start: number; end: number },
  source: string,
  knownClasses: Set<string>,
  ignorePrefixes: readonly string[],
  out: LintDiagnostic[],
): void {
  const value = attrs.class;
  if (!value) return;

  for (const name of value.split(/\s+/).filter(Boolean)) {
    if (knownClasses.has(name)) continue;
    if (ignorePrefixes.some((prefix) => name.startsWith(prefix))) continue;
    out.push({
      severity: 'warning',
      rule: 'undefined-class',
      message: `Nothing in this site's CSS defines ".${name}", so it has no effect.`,
      ...rangeOf(source, tag, name),
    });
  }
}

function checkDuplicateId(
  attrs: Record<string, string>,
  tag: { start: number; end: number },
  seen: Map<string, number>,
  out: LintDiagnostic[],
): void {
  const id = attrs.id;
  if (!id) return;
  const previous = seen.get(id);
  if (previous !== undefined) {
    out.push({
      severity: 'warning',
      rule: 'duplicate-id',
      // Duplicate ids break anchor links, `<label for>` and the tabs/modal
      // blocks, all of which resolve by id.
      message: `The id "${id}" is used more than once on this page. Anchor links and labels will target the first one.`,
      ...tag,
    });
  } else {
    seen.set(id, tag.start);
  }
}

function checkAccessibility(
  el: Element,
  attrs: Record<string, string>,
  tag: { start: number; end: number },
  out: LintDiagnostic[],
): void {
  if (el.name === 'img' && attrs.alt === undefined) {
    out.push({
      severity: 'warning',
      rule: 'img-no-alt',
      message: 'This image has no alt text. Add one, or set alt="" if it is purely decorative.',
      ...tag,
    });
  }

  if (el.name === 'a' && attrs.target === '_blank' && !(attrs.rel ?? '').includes('noopener')) {
    out.push({
      severity: 'warning',
      rule: 'blank-no-noopener',
      message: 'A link opening in a new tab should carry rel="noopener" so the new page cannot reach back into this one.',
      ...tag,
    });
  }
}

/**
 * A diagnostic resolved to line/column. Converting offsets happens in the worker
 * so the main thread only has numbers to hand to `setModelMarkers`.
 */
export interface LintMarker extends LintDiagnostic {
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
}

/**
 * Resolve every diagnostic's offsets against one shared line table, rather than
 * rescanning the document per diagnostic — on a long page with many warnings the
 * naive form is quadratic.
 */
export function toMarkers(source: string, diagnostics: readonly LintDiagnostic[]): LintMarker[] {
  if (diagnostics.length === 0) return [];
  const lineStarts = [0];
  for (let i = 0; i < source.length; i++) {
    if (source.charCodeAt(i) === 10) lineStarts.push(i + 1);
  }

  const position = (offset: number) => {
    const clamped = Math.max(0, Math.min(offset, source.length));
    // Binary search for the last line start at or before the offset.
    let low = 0;
    let high = lineStarts.length - 1;
    while (low < high) {
      const mid = (low + high + 1) >> 1;
      if (lineStarts[mid]! <= clamped) low = mid;
      else high = mid - 1;
    }
    return { line: low + 1, column: clamped - lineStarts[low]! + 1 };
  };

  return diagnostics.map((diagnostic) => {
    const start = position(diagnostic.start);
    const end = position(diagnostic.end);
    return {
      ...diagnostic,
      startLine: start.line,
      startColumn: start.column,
      endLine: end.line,
      endColumn: end.column,
    };
  });
}
