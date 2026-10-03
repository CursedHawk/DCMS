import { definitionFor, walk, type Action, type Node, type Registry, type Responsive, type When } from '@dcms/site-runtime';
import type { Component } from 'grapesjs';

/**
 * A page's node tree ↔ GrapesJS's component tree.
 *
 * GrapesJS holds only the page being edited, and nothing it persists is ever saved (ADR 0020):
 * the file is the node tree, and this module is the whole translation. Each node becomes a
 * component of the node's own type; each declared slot becomes a fixed `dcms-slot` child of it,
 * which is what lets GrapesJS's own drag and drop put children into named slots.
 *
 * Two things survive a round trip that the canvas cannot show, so that opening and editing a
 * page never loses what someone else wrote:
 * - a node of a type this registry does not know is kept verbatim (`RAW`) and drawn as a
 *   problem box;
 * - children in a slot the component no longer declares are kept aside (`KEEP`) and written
 *   back where they were.
 */

export const SLOT_TYPE = 'dcms-slot';
export const UNKNOWN_TYPE = 'dcms-unknown';

/** Component properties the adapter owns. Prefixed so they cannot meet a GrapesJS one. */
export const ID = 'dcmsId';
export const PROPS = 'dcmsProps';
export const EXTRA = 'dcmsExtra';
export const KEEP = 'dcmsKeep';
export const SLOT = 'dcmsSlot';
export const RAW = 'dcmsRaw';

/** The node fields the canvas does not edit yet but must carry through unchanged. */
export interface NodeExtra {
  version?: number;
  responsive?: Responsive;
  action?: Action;
  bind?: Record<string, string>;
  css?: string;
  when?: When;
}

type GrapesDefinition = Record<string, unknown>;

export function toGrapes(node: Node, registry: Registry): GrapesDefinition {
  const definition = definitionFor(registry, node);
  if (!definition) return { type: UNKNOWN_TYPE, [RAW]: node };

  const declared = new Set((definition.slots ?? []).map((s) => s.name));
  const keep = Object.fromEntries(Object.entries(node.slots ?? {}).filter(([name]) => !declared.has(name)));
  const extra: NodeExtra = {};
  if (node.version !== undefined) extra.version = node.version;
  if (node.responsive !== undefined) extra.responsive = node.responsive;
  if (node.action !== undefined) extra.action = node.action;
  if (node.bind !== undefined) extra.bind = node.bind;
  if (node.css !== undefined) extra.css = node.css;
  if (node.when !== undefined) extra.when = node.when;

  return {
    type: node.type,
    [ID]: node.id,
    [PROPS]: node.props ?? {},
    [EXTRA]: extra,
    ...(Object.keys(keep).length ? { [KEEP]: keep } : {}),
    components: (definition.slots ?? []).map((slot) => ({
      type: SLOT_TYPE,
      [SLOT]: slot.name,
      name: slot.label ?? slot.name,
      components: (node.slots?.[slot.name] ?? []).map((child) => toGrapes(child, registry)),
    })),
  };
}

/**
 * The node a component stands for.
 *
 * `seen` collects ids across the whole tree. GrapesJS's copy and paste clones a component with
 * every property — our id included — so the second holder of an id is given a fresh one here,
 * on the model too, before it can be saved as an ambiguity.
 */
export function fromGrapes(component: Component, seen: Set<string> = new Set()): Node {
  if (component.get('type') === UNKNOWN_TYPE) {
    const raw = component.get(RAW) as Node;
    for (const node of walk(raw)) seen.add(node.id);
    return raw;
  }

  let id = component.get(ID) as string | undefined;
  if (!id || seen.has(id)) {
    id = newNodeId(seen);
    component.set(ID, id, { silent: true });
  }
  seen.add(id);

  const slots: Record<string, Node[]> = {};
  for (const slot of component.components().models) {
    if (slot.get('type') !== SLOT_TYPE) continue;
    const children = slot.components().models.map((child) => fromGrapes(child, seen));
    if (children.length) slots[slot.get(SLOT) as string] = children;
  }
  for (const [name, children] of Object.entries((component.get(KEEP) ?? {}) as Record<string, Node[]>)) {
    if (children.length) slots[name] = children;
  }

  const extra = (component.get(EXTRA) ?? {}) as NodeExtra;
  return canonicalNode({
    id,
    type: component.get('type') as string,
    version: extra.version,
    props: component.get(PROPS) as Record<string, unknown> | undefined,
    slots,
    responsive: extra.responsive,
    action: extra.action,
    bind: extra.bind,
    css: extra.css,
    when: extra.when,
  });
}

/**
 * One spelling per node: fields in a fixed order, empty props and empty slots left out. What
 * the canvas writes is therefore stable, so saving a page nobody changed changes nothing, and
 * a diff shows the edit rather than a reshuffle.
 */
export function canonicalNode(node: Node): Node {
  const out: Node = { id: node.id, type: node.type };
  if (node.version !== undefined) out.version = node.version;
  if (node.props && Object.keys(node.props).length) out.props = node.props;
  if (node.slots && Object.keys(node.slots).length) out.slots = node.slots;
  if (node.responsive !== undefined) out.responsive = node.responsive;
  if (node.action !== undefined) out.action = node.action;
  if (node.bind !== undefined && Object.keys(node.bind).length) out.bind = node.bind;
  if (node.css) out.css = node.css;
  if (node.when !== undefined) out.when = node.when;
  return out;
}

const ID_ALPHABET = 'abcdefghijklmnopqrstuvwxyz0123456789';

/** `n` + 8 random characters — short enough to read in a diff, wide enough not to collide. */
export function newNodeId(taken?: ReadonlySet<string>): string {
  for (;;) {
    const bytes = crypto.getRandomValues(new Uint8Array(8));
    const id = `n${Array.from(bytes, (b) => ID_ALPHABET[b % ID_ALPHABET.length]).join('')}`;
    if (!taken?.has(id)) return id;
  }
}
