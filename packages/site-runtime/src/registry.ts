import type { CSSProperties, ComponentType, ReactNode } from 'react';
import type { Action, ActionType } from './actions';
import type { Responsive } from './document';
import { COMPONENT_TYPE, NAME } from './ids';
import { propDefinitionSchema, propValueSchema, type PropDefinition } from './props';
import type { TenantComponentDoc } from './tenant';

/**
 * The component registry: what each type is, what it accepts, and the one rule for where it
 * may go.
 *
 * A React component is not automatically a builder component. One that takes callbacks,
 * render props or arbitrary children cannot be placed by a person dragging things around or by
 * a model emitting JSON. A definition is the serialisable contract on top: named props with
 * editor controls, named slots that say what they accept, and nothing else.
 */

export interface SlotDefinition {
  name: string;
  label?: string;
  /** The component types this slot accepts. Absent means any type that may be placed there. */
  allowed?: readonly string[];
  /** The most children it holds. Absent means unbounded. */
  max?: number;
}

/**
 * How a slot lays out its children. The slot is a real element (`.dcms-slot`) in the editor
 * and on the site alike, so a component that arranges its children — a stack, a grid — puts
 * that layout here rather than on an element of its own: the slot is their parent.
 */
export interface SlotLayout {
  className?: string;
  style?: CSSProperties;
}

/** What a component's React implementation receives. */
export interface ComponentRenderProps<P = Record<string, unknown>> {
  nodeId: string;
  props: P;
  /** The node's action, if it has one. Run it only when `useRenderMode()` is `live`. */
  action?: Action;
  /**
   * Tablet and mobile overrides of `props`, for props declared `responsive`. Rendered as
   * `t-`/`m-` classes (see `variants` in components.tsx and RESPONSIVE_RULES in styles.ts), so a
   * device switch on the canvas and a real phone get the same CSS.
   */
  responsive?: Responsive;
  /**
   * One of the component's slots, as an element. The published runtime renders the slot's
   * child nodes into it; the editor hands it to GrapesJS, which draws and drops them there. A
   * component never knows which, and so cannot behave differently on the canvas than on the
   * site.
   */
  slot: (name: string, layout?: SlotLayout) => ReactNode;
}

export interface ComponentDefinition {
  type: string;
  /** Bumped on any change to props or slots that an existing instance could notice. */
  version: number;
  label: string;
  /** What it is for, in a sentence an author with no web background understands. */
  description?: string;
  category: string;
  /** Other words people search for it by ("photo" for Image, "columns" for Grid). */
  keywords?: readonly string[];
  icon?: string;
  component: ComponentType<ComponentRenderProps<any>>;
  props: readonly PropDefinition[];
  slots?: readonly SlotDefinition[];
  /** Earlier versions of a tenant component, so instances pinned to one keep rendering. */
  olderVersions?: Readonly<Record<number, ComponentDefinition>>;
  /** The document a tenant component was made from; absent for built-ins. */
  template?: TenantComponentDoc;
  /** The actions an author may give an instance (`node.action`). Absent means none. */
  actions?: readonly ActionType[];
  /** The only parent types this may be placed in. Absent means anywhere a slot accepts it. */
  allowedParents?: readonly string[];
  /** False for structural pieces that exist only where the platform puts them — the shell's outlet. */
  draggable?: boolean;
  /**
   * `interactive` components take clicks on the canvas (an accordion that must open to be
   * edited); `select-first`, the default, swallow them so a click selects.
   */
  interaction?: 'select-first' | 'interactive';
  /**
   * What a freshly placed one starts with, per slot — an accordion arrives with two questions,
   * not empty. Only for new placements from the palette; a loaded node keeps what it has.
   */
  starter?: Readonly<Record<string, readonly StarterNode[]>>;
}

/** A child a new component starts with: its type, props beyond the defaults, its own starters. */
export interface StarterNode {
  type: string;
  props?: Record<string, unknown>;
  slots?: Readonly<Record<string, readonly StarterNode[]>>;
}

export type Registry = ReadonlyMap<string, ComponentDefinition>;

/**
 * Build a registry, refusing definitions that would fail later and further from their cause:
 * a duplicate type, a malformed prop, a default its own prop would reject, a slot that names a
 * type nobody registered.
 */
export function createRegistry(definitions: readonly ComponentDefinition[]): Registry {
  const registry = new Map<string, ComponentDefinition>();
  for (const def of definitions) {
    const where = `component “${def.type}”`;
    if (!COMPONENT_TYPE.test(def.type)) throw new Error(`${where}: type must be namespace.name`);
    if (registry.has(def.type)) throw new Error(`${where} is registered twice`);
    if (!Number.isInteger(def.version) || def.version < 1) throw new Error(`${where}: version must be a positive integer`);

    const propNames = new Set<string>();
    for (const prop of def.props) {
      const parsed = propDefinitionSchema.safeParse(prop);
      if (!parsed.success) throw new Error(`${where}, prop “${prop.name}”: ${parsed.error.issues[0]?.message}`);
      if (propNames.has(prop.name)) throw new Error(`${where}: prop “${prop.name}” is declared twice`);
      propNames.add(prop.name);
      if ('default' in prop && prop.default !== undefined && !propValueSchema(prop).safeParse(prop.default).success) {
        throw new Error(`${where}, prop “${prop.name}”: its default is not a valid value`);
      }
    }

    const slotNames = new Set<string>();
    for (const slot of def.slots ?? []) {
      if (!NAME.test(slot.name)) throw new Error(`${where}: slot name “${slot.name}” is not a valid name`);
      if (slotNames.has(slot.name)) throw new Error(`${where}: slot “${slot.name}” is declared twice`);
      slotNames.add(slot.name);
    }
    registry.set(def.type, def);
  }

  // Only once every type is known, so definitions may name each other in any order.
  for (const def of registry.values()) {
    const named = [...(def.slots ?? []).flatMap((s) => s.allowed ?? []), ...(def.allowedParents ?? [])];
    const unknown = named.find((type) => !registry.has(type));
    if (unknown) throw new Error(`component “${def.type}” refers to “${unknown}”, which is not registered`);
  }
  return registry;
}

export type Placement = { ok: true } | { ok: false; reason: string };

function list(types: readonly string[], registry: Registry): string {
  return types.map((t) => registry.get(t)?.label ?? t).join(', ');
}

/**
 * May a `childType` go into `parentType`'s `slot`?
 *
 * **The** placement rule. Drag and drop, the inspector, the component composer, the AI tools,
 * the site validator and the publisher all ask this function, so anything one of them refuses
 * the rest refuse too — an AI cannot build a page a person could not, and a page that opened
 * in the builder cannot fail to publish over placement.
 *
 * `siblings` is how many children the slot already holds *not counting the one being placed*:
 * a move within the same slot passes the count without the moving node.
 *
 * The reason is a sentence for a person — the inspector shows it and the AI reads it.
 */
export function canPlace(
  registry: Registry,
  parentType: string,
  slotName: string,
  childType: string,
  siblings = 0,
): Placement {
  const parent = registry.get(parentType);
  if (!parent) return { ok: false, reason: `“${parentType}” is not a known component.` };
  const child = registry.get(childType);
  if (!child) return { ok: false, reason: `“${childType}” is not a known component.` };

  const slot = parent.slots?.find((s) => s.name === slotName);
  if (!slot) {
    const names = (parent.slots ?? []).map((s) => s.name);
    return {
      ok: false,
      reason: names.length
        ? `${parent.label} has no slot “${slotName}” (it has ${names.join(', ')}).`
        : `${parent.label} cannot contain other components.`,
    };
  }
  const slotLabel = `${parent.label} › ${slot.label ?? slot.name}`;

  if (slot.allowed && !slot.allowed.includes(childType)) {
    return { ok: false, reason: `${slotLabel} accepts only ${list(slot.allowed, registry)}, not ${child.label}.` };
  }
  if (child.allowedParents?.length === 0) {
    return { ok: false, reason: `${child.label} cannot be placed inside another component.` };
  }
  if (child.allowedParents && !child.allowedParents.includes(parentType)) {
    return { ok: false, reason: `${child.label} can only be placed inside ${list(child.allowedParents, registry)}.` };
  }
  if (slot.max !== undefined && siblings >= slot.max) {
    return { ok: false, reason: `${slotLabel} holds at most ${slot.max}.` };
  }
  return { ok: true };
}
