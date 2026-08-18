import { COMPONENT_ATTR, identityClassOf, type DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Component identification when markup is read back.
 *
 * GrapesJS asks each registered type "is this node yours?" twice over, through
 * two different shapes: `isParsedNode` receives the plain object tree our worker
 * produces, `isComponent` receives a DOM element (or GrapesJS's synthetic stand-
 * in for one). Both are implemented, delegating to a single matcher, so a page
 * keeps its component types whichever path parsed it — a mismatch between the
 * two is exactly how a Hero silently degrades to an anonymous `<section>` after
 * a round trip.
 */

/** The subset of a node both shapes can answer. */
export interface NodeView {
  tag: string;
  classes: Set<string>;
  attributes: Record<string, string>;
}

interface ParsedNodeLike {
  tagName?: string;
  attributes?: Record<string, string>;
}

interface ElementLike {
  tagName?: string;
  getAttribute?: (name: string) => string | null;
}

export function viewOfParsedNode(node: ParsedNodeLike): NodeView {
  const attributes = node.attributes ?? {};
  return {
    tag: (node.tagName ?? '').toLowerCase(),
    classes: classSet(attributes.class),
    attributes,
  };
}

export function viewOfElement(el: ElementLike): NodeView {
  // GrapesJS's SyntheticElement uppercases tagName to mirror the DOM, and a real
  // HTMLElement does the same, so both need lowering before comparison.
  const tag = (el.tagName ?? '').toLowerCase();
  const get = (name: string) => el.getAttribute?.(name) ?? null;
  const attributes: Record<string, string> = {};
  for (const name of ['class', COMPONENT_ATTR]) {
    const value = get(name);
    if (value !== null) attributes[name] = value;
  }
  return { tag, classes: classSet(attributes.class), attributes };
}

function classSet(value: string | undefined): Set<string> {
  return new Set((value ?? '').split(/\s+/).filter(Boolean));
}

/**
 * Does this node belong to `spec`?
 *
 * A plugin component is identified by its `data-dcms-component` value, because
 * that attribute is already the published contract hydrate.js reads. Everything
 * else is identified by tag plus its identity class.
 */
export function matchesSpec(view: NodeView, spec: DcmsComponentSpec): boolean {
  if (spec.category === 'plugin') {
    return view.attributes[COMPONENT_ATTR] === spec.type;
  }
  if (spec.tag && view.tag !== spec.tag.toLowerCase()) return false;
  return view.classes.has(identityClassOf(spec));
}

/** The `isComponent`/`isParsedNode` pair for a spec, ready to hand to GrapesJS. */
export function matchersFor(spec: DcmsComponentSpec) {
  return {
    isComponent: (el: ElementLike) => (matchesSpec(viewOfElement(el), spec) ? { type: spec.type } : undefined),
    isParsedNode: (node: ParsedNodeLike) =>
      matchesSpec(viewOfParsedNode(node), spec) ? { type: spec.type } : undefined,
  };
}
