import { useContext } from 'react';
import { z } from 'zod';
import { nodeSchema, walk, type Node } from './document';
import { NAME, isInternalPath, isSafeExternalHref } from './ids';
import { propDefinitionSchema } from './props';
import { createRegistry, type ComponentDefinition, type ComponentRenderProps, type Registry } from './registry';
import { RenderNode } from './render';
import { RegistryContext, TemplateScopeContext, type TemplateScope } from './scope';

/**
 * Components a site builds for itself, out of other components (ADR 0020, P3).
 *
 * A tenant component is **data**: a template tree of existing components, the settings it
 * exposes (each wired to a prop of a node inside it, or to the link of a button inside it) and the
 * slots it exposes (each wired to a slot of a node inside it). The runtime renders it with its own
 * code, so it is safe to draw on the canvas, which shares the admin's origin — no tenant code ever
 * runs there.
 *
 * Each version is its own immutable file, `dcms/components/<name>/v<N>.json`, and an instance pins
 * the version it was made with (`{ type: "tenant.<name>", version: N }`). Changing a component
 * that pages already use makes a new version; pages move to it when someone updates them.
 */

const COMPONENT_FILE = /^dcms\/components\/([a-z0-9][a-z0-9-]*)\/v([1-9][0-9]*)\.json$/;

export function componentPath(name: string, version: number): string {
  return `dcms/components/${name}/v${version}.json`;
}

export function componentFileOf(path: string): { name: string; version: number } | null {
  const m = COMPONENT_FILE.exec(path);
  return m ? { name: m[1]!, version: Number(m[2]) } : null;
}

export const TENANT_PREFIX = 'tenant.';

export function tenantType(name: string): string {
  return `${TENANT_PREFIX}${name}`;
}

const bindingSchema = z.union([
  z.strictObject({ node: z.string(), prop: z.string().regex(NAME) }),
  /** A `url` setting that becomes the link (navigate / open-external) of a node inside. */
  z.strictObject({ node: z.string(), action: z.literal('link') }),
]);

export const tenantComponentSchema = z
  .strictObject({
    schemaVersion: z.literal(1),
    name: z.string().regex(/^[a-z0-9][a-z0-9-]{0,47}$/, 'must be kebab-case'),
    version: z.number().int().positive(),
    label: z.string().min(1).max(60),
    description: z.string().max(300).optional(),
    category: z.string().max(40).optional(),
    props: z.array(propDefinitionSchema).default([]),
    slots: z
      .array(
        z.strictObject({
          name: z.string().regex(NAME),
          label: z.string().max(60).optional(),
          allowed: z.array(z.string()).optional(),
          max: z.number().int().positive().optional(),
        }),
      )
      .default([]),
    /** Exposed prop name → where its value goes inside. */
    bindings: z.record(z.string().regex(NAME), z.array(bindingSchema)).default({}),
    /** Exposed slot name → the inner node's slot its children are drawn in. */
    slotTargets: z.record(z.string().regex(NAME), z.strictObject({ node: z.string(), slot: z.string().regex(NAME) })).default({}),
    root: nodeSchema,
  })
  .superRefine((doc, ctx) => {
    const ids = new Set<string>();
    for (const node of walk(doc.root)) {
      if (ids.has(node.id)) ctx.addIssue({ code: 'custom', path: ['root'], message: `node id “${node.id}” is used more than once` });
      ids.add(node.id);
    }
    const props = new Set(doc.props.map((p) => p.name));
    for (const [prop, targets] of Object.entries(doc.bindings)) {
      if (!props.has(prop)) ctx.addIssue({ code: 'custom', path: ['bindings', prop], message: `“${prop}” is not one of the component’s settings` });
      for (const target of targets) {
        if (!ids.has(target.node)) ctx.addIssue({ code: 'custom', path: ['bindings', prop], message: `there is no node “${target.node}” inside` });
      }
    }
    const slots = new Set(doc.slots.map((s) => s.name));
    const nodes = new Map([...walk(doc.root)].map((n) => [n.id, n]));
    for (const [slot, target] of Object.entries(doc.slotTargets)) {
      if (!slots.has(slot)) ctx.addIssue({ code: 'custom', path: ['slotTargets', slot], message: `“${slot}” is not one of the component’s slots` });
      const node = nodes.get(target.node);
      if (!node) ctx.addIssue({ code: 'custom', path: ['slotTargets', slot], message: `there is no node “${target.node}” inside` });
      // The instance's children *are* that slot's content — on the canvas it is GrapesJS's own slot
      // element — so the template cannot also put children there; they would never be drawn.
      else if (node.slots?.[target.slot]?.length) {
        ctx.addIssue({ code: 'custom', path: ['slotTargets', slot], message: `the slot “${slot}” must be wired to an empty slot inside` });
      }
    }
    for (const slot of doc.slots) {
      if (!doc.slotTargets[slot.name]) ctx.addIssue({ code: 'custom', path: ['slots'], message: `the slot “${slot.name}” is not placed anywhere inside` });
    }
  });

export type TenantComponentDoc = z.infer<typeof tenantComponentSchema>;

/** The template with the instance's settings written into the nodes they are wired to. */
export function expandTemplate(doc: TenantComponentDoc, props: Readonly<Record<string, unknown>>, responsive?: Node['responsive']): Node {
  const root = structuredClone(doc.root) as Node;
  const byId = new Map<string, Node>();
  for (const node of walk(root)) byId.set(node.id, node);

  for (const prop of doc.props) {
    const value = props[prop.name] ?? ('default' in prop ? prop.default : undefined);
    for (const target of doc.bindings[prop.name] ?? []) {
      const node = byId.get(target.node);
      if (!node) continue;
      if ('action' in target) {
        if (typeof value !== 'string' || !value) continue;
        if (isInternalPath(value)) node.action = { type: 'navigate', to: value };
        else if (isSafeExternalHref(value)) node.action = { type: 'open-external', href: value };
        continue;
      }
      if (value !== undefined) node.props = { ...node.props, [target.prop]: value };
      for (const device of ['tablet', 'mobile'] as const) {
        const override = responsive?.[device]?.[prop.name];
        if (override === undefined) continue;
        node.responsive = { ...node.responsive, [device]: { ...node.responsive?.[device], [target.prop]: override } };
      }
    }
  }
  return root;
}

function makeImplementation(doc: TenantComponentDoc) {
  const targets = new Map(Object.entries(doc.slotTargets).map(([exposed, t]) => [`${t.node}\u0000${t.slot}`, exposed]));
  return function TenantComponent({ props, responsive, slot }: ComponentRenderProps) {
    const registry = useContext(RegistryContext);
    const outer = useContext(TemplateScopeContext);
    if (!registry) return null;
    const scope: TemplateScope = {
      slotFor: (nodeId, innerSlot, layout) => {
        const exposed = targets.get(`${nodeId}\u0000${innerSlot}`);
        if (exposed === undefined) return undefined;
        return <TemplateScopeContext.Provider value={outer}>{slot(exposed, layout)}</TemplateScopeContext.Provider>;
      },
    };
    return (
      <TemplateScopeContext.Provider value={scope}>
        <RenderNode node={expandTemplate(doc, props, responsive)} registry={registry} />
      </TemplateScopeContext.Provider>
    );
  };
}

/**
 * Registry definitions for a site's components: one per component, at its latest version, with
 * every earlier version reachable through `olderVersions` so pinned instances keep rendering.
 */
export function tenantDefinitions(docs: readonly TenantComponentDoc[]): ComponentDefinition[] {
  const byName = new Map<string, TenantComponentDoc[]>();
  for (const doc of docs) byName.set(doc.name, [...(byName.get(doc.name) ?? []), doc]);
  const out: ComponentDefinition[] = [];
  for (const versions of byName.values()) {
    versions.sort((a, b) => a.version - b.version);
    const toDefinition = (doc: TenantComponentDoc): ComponentDefinition => ({
      type: tenantType(doc.name),
      version: doc.version,
      label: doc.label,
      description: doc.description,
      category: doc.category || 'My components',
      component: makeImplementation(doc),
      props: doc.props,
      slots: doc.slots,
      template: doc,
    });
    const latest = toDefinition(versions.at(-1)!);
    latest.olderVersions = Object.fromEntries(versions.slice(0, -1).map((d) => [d.version, toDefinition(d)]));
    out.push(latest);
  }
  return out;
}

/** Every tenant component a template uses, directly or through others — for the cycle check. */
export function componentDependencies(doc: TenantComponentDoc): Set<string> {
  const out = new Set<string>();
  for (const node of walk(doc.root)) if (node.type.startsWith(TENANT_PREFIX)) out.add(node.type);
  return out;
}

/**
 * A site's registry: the built-ins plus its own components. Built defensively — one component
 * whose definition the registry refuses (a slot naming a type nobody has) is left out and
 * reported, rather than taking every page of the site down with it.
 */
export function siteRegistry(
  builtins: readonly ComponentDefinition[],
  docs: readonly TenantComponentDoc[],
): { registry: Registry; problems: string[] } {
  const tenant = tenantDefinitions(docs);
  try {
    return { registry: createRegistry([...builtins, ...tenant]), problems: [] };
  } catch {
    const problems: string[] = [];
    const ok = tenant.filter((def) => {
      try {
        createRegistry([...builtins, def]);
        return true;
      } catch (e) {
        problems.push(`${def.type}: ${(e as Error).message}`);
        return false;
      }
    });
    try {
      return { registry: createRegistry([...builtins, ...ok]), problems };
    } catch (e) {
      return { registry: createRegistry(builtins), problems: [...problems, (e as Error).message] };
    }
  }
}

/** Every component document in a file map (path → parsed JSON), with what could not be read. */
export function readComponentDocs(byPath: ReadonlyMap<string, unknown>): { docs: TenantComponentDoc[]; problems: { path: string; message: string }[] } {
  const docs: TenantComponentDoc[] = [];
  const problems: { path: string; message: string }[] = [];
  for (const [path, json] of byPath) {
    const file = componentFileOf(path);
    if (!file) continue;
    const parsed = tenantComponentSchema.safeParse(json);
    if (!parsed.success) {
      const issue = parsed.error.issues[0];
      problems.push({ path, message: `${issue?.path.join('.') || 'component'}: ${issue?.message}` });
    } else if (parsed.data.name !== file.name || parsed.data.version !== file.version) {
      problems.push({ path, message: `says it is ${parsed.data.name} v${parsed.data.version}, but the file is ${file.name} v${file.version}.` });
    } else {
      docs.push(parsed.data);
    }
  }
  return { docs, problems };
}
