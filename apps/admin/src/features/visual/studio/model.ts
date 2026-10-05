import { walk, type ComponentDefinition, type Node, type PropDefinition, type Registry, type TenantComponentDoc } from '@dcms/site-runtime';

/**
 * What the component studio (Mode D v2, U4) does to a component's document: pure functions, one
 * edit each, shared by the studio's designers and the inspector's Expose shortcuts.
 */

function uniqueName(base: string, taken: ReadonlySet<string>): string {
  let name = base;
  for (let n = 2; taken.has(name); n++) name = `${base}${n}`;
  return name;
}

/** The setting name that carries `prop` of node `nodeId`, if it is exposed. */
export function exposedAs(doc: TenantComponentDoc, nodeId: string, prop: string): string | undefined {
  return Object.entries(doc.bindings).find(([, ts]) => ts.some((b) => b.node === nodeId && 'prop' in b && b.prop === prop))?.[0];
}

export function linkExposedAs(doc: TenantComponentDoc, nodeId: string): string | undefined {
  return Object.entries(doc.bindings).find(([, ts]) => ts.some((b) => b.node === nodeId && 'action' in b))?.[0];
}

export function slotExposedAs(doc: TenantComponentDoc, nodeId: string, slot: string): string | undefined {
  return Object.entries(doc.slotTargets).find(([, s]) => s.node === nodeId && s.slot === slot)?.[0];
}

/** Let pages set `prop` of an inner node: a new setting, starting at the value it has now. */
export function exposeProp(doc: TenantComponentDoc, nodeId: string, owner: ComponentDefinition, prop: PropDefinition, current: unknown): TenantComponentDoc {
  const name = uniqueName(prop.name, new Set(doc.props.map((p) => p.name)));
  const exposed = { ...prop, name, label: `${owner.label}: ${prop.label}` } as PropDefinition;
  if (current !== undefined && 'default' in exposed) (exposed as { default?: unknown }).default = current;
  return { ...doc, props: [...doc.props, exposed], bindings: { ...doc.bindings, [name]: [{ node: nodeId, prop: prop.name }] } };
}

export function exposeLink(doc: TenantComponentDoc, nodeId: string, label: string): TenantComponentDoc {
  const name = uniqueName('link', new Set(doc.props.map((p) => p.name)));
  return { ...doc, props: [...doc.props, { kind: 'url', name, label }], bindings: { ...doc.bindings, [name]: [{ node: nodeId, action: 'link' }] } };
}

export function unexpose(doc: TenantComponentDoc, name: string): TenantComponentDoc {
  const bindings = { ...doc.bindings };
  delete bindings[name];
  return { ...doc, props: doc.props.filter((p) => p.name !== name), bindings };
}

/** Change how a setting is presented to pages: its label, help, group. */
export function editSetting(doc: TenantComponentDoc, name: string, patch: Partial<Pick<PropDefinition, 'label' | 'description' | 'group'>>): TenantComponentDoc {
  return { ...doc, props: doc.props.map((p) => (p.name === name ? ({ ...p, ...patch } as PropDefinition) : p)) };
}

/** Move a setting (or slot) one place earlier or later: the order the inspector shows them in. */
function shift<T extends { name: string }>(list: readonly T[], name: string, by: -1 | 1): T[] {
  const i = list.findIndex((x) => x.name === name);
  const j = i + by;
  if (i < 0 || j < 0 || j >= list.length) return [...list];
  const out = [...list];
  [out[i], out[j]] = [out[j]!, out[i]!];
  return out;
}

export function moveSetting(doc: TenantComponentDoc, name: string, by: -1 | 1): TenantComponentDoc {
  return { ...doc, props: shift(doc.props, name, by) };
}

/** Let pages fill an empty slot of an inner node. */
export function exposeSlot(doc: TenantComponentDoc, nodeId: string, slot: string, label: string): TenantComponentDoc {
  const name = uniqueName(slot === 'default' ? 'content' : slot, new Set(doc.slots.map((s) => s.name)));
  return { ...doc, slots: [...doc.slots, { name, label }], slotTargets: { ...doc.slotTargets, [name]: { node: nodeId, slot } } };
}

export function unexposeSlot(doc: TenantComponentDoc, name: string): TenantComponentDoc {
  const slotTargets = { ...doc.slotTargets };
  delete slotTargets[name];
  return { ...doc, slots: doc.slots.filter((s) => s.name !== name), slotTargets };
}

type SlotDoc = TenantComponentDoc['slots'][number];

/** What a page may put in a slot: its label, which kinds of part (none = any), how many. */
export function editSlot(doc: TenantComponentDoc, name: string, patch: Partial<Omit<SlotDoc, 'name'>>): TenantComponentDoc {
  return {
    ...doc,
    slots: doc.slots.map((s) => {
      if (s.name !== name) return s;
      const next = { ...s, ...patch };
      if (!next.allowed?.length) delete next.allowed;
      if (!next.max) delete next.max;
      return next;
    }),
  };
}

// --- What the designers list --------------------------------------------------------------

export interface NodeEntry {
  node: Node;
  definition: ComponentDefinition;
  /** "Heading “Welcome”": enough to tell two parts of the same kind apart. */
  title: string;
}

/** Every part of the template, outermost first, with a title an author recognises. */
export function templateParts(doc: TenantComponentDoc, registry: Registry): NodeEntry[] {
  const out: NodeEntry[] = [];
  for (const node of walk(doc.root)) {
    const definition = registry.get(node.type);
    if (!definition) continue;
    const sample = ['text', 'label', 'title', 'alt'].map((k) => node.props?.[k]).find((v): v is string => typeof v === 'string' && v.trim() !== '');
    out.push({ node, definition, title: sample ? `${definition.label} “${sample.length > 28 ? `${sample.slice(0, 27)}…` : sample}”` : definition.label });
  }
  return out;
}

/** The settings worth offering first: what a page would want to change about each part. */
export const LIKELY_GROUPS = new Set(['content', 'behaviour', 'data']);

export interface SlotEntry {
  part: NodeEntry;
  slot: string;
  label: string;
  /** Empty slots can become places pages fill; one with parts in it cannot (yet). */
  empty: boolean;
}

export function templateSlots(doc: TenantComponentDoc, registry: Registry): SlotEntry[] {
  return templateParts(doc, registry).flatMap((part) =>
    (part.definition.slots ?? []).map((s) => ({ part, slot: s.name, label: s.label ?? s.name, empty: (part.node.slots?.[s.name] ?? []).length === 0 })),
  );
}
