import { DESIGN_KITS, applyKit, findKit } from '@dcms/gjs-blocks';
import {
  APP_JSON,
  BUILTIN_COMPONENTS,
  THEME_JSON,
  actionSchema,
  appSchema,
  canPlace,
  checkVisualSite,
  componentFileOf,
  componentPath,
  definitionFor,
  formatProblems,
  nodeSchema,
  pageIdFromPath,
  pagePath,
  pageSchema,
  readComponentDocs,
  siteRegistry,
  sourceSchema,
  tenantComponentSchema,
  tenantType,
  themeTokensSchema,
  walk,
  type App,
  type Node,
  type Page,
  type Registry,
  type TenantComponentDoc,
} from '@dcms/site-runtime';
import type { ToolOutcome, ToolSpec } from '../../agent/contracts';
import type { TenantToolContext } from '../../ide/agent/tenantTools';
import { useVfs } from '../../site-source/vfs';
import { newNodeId } from '../canvas/tree';
import { defaultShell, migrateInstance, pageIdFor, pruneComponent } from '../documents';
import { serializeDoc } from '../starter';
import { useVisual } from '../store';

/**
 * The Mode D agent's tools (P5): structured edits of the site's documents, never free-form text.
 *
 * Every write re-validates the whole document it produces with the schemas the builder and the
 * published site load with, and checks placement with `canPlace` — the rule drag and drop uses —
 * so the agent cannot build a page a person could not. Writes go through the run's transaction
 * (`ctx.tx`), which is what puts them in the change-review pane and makes them revertable; the
 * risk table in `modes.ts` decides what asks first.
 *
 * Documents are addressed as `page:<id>`, `shell` (the app shell in app.json) or
 * `component:<name>@<version>`.
 */

type Tool = ToolSpec<TenantToolContext>;

function files(): Readonly<Record<string, string>> {
  return useVfs.getState().files;
}

function err(content: string): ToolOutcome {
  return { content, isError: true };
}

function json(text: string | undefined): unknown {
  try {
    return JSON.parse(text ?? '');
  } catch {
    return undefined;
  }
}

/** The registry the site renders with: built-ins plus its own components. */
export function registryFromFiles(all: Readonly<Record<string, string>> = files()): Registry {
  const byPath = new Map<string, unknown>();
  for (const [path, text] of Object.entries(all)) if (componentFileOf(path)) byPath.set(path, json(text));
  return siteRegistry(BUILTIN_COMPONENTS, readComponentDocs(byPath).docs).registry;
}

// ---------------------------------------------------------------------------
// Documents
// ---------------------------------------------------------------------------

interface LoadedDoc {
  path: string;
  /** The file's text as it is now, or undefined for a shell not yet written. */
  text: string | undefined;
  root: Node;
  /** The component version this is, when it is one. */
  component?: TenantComponentDoc;
  /** The new file text for an edited tree — validated, or the reason it is not valid. */
  build(root: Node): { ok: true; text: string } | { ok: false; error: string };
}

function issue(e: { issues: { path: PropertyKey[]; message: string }[] }): string {
  const i = e.issues[0];
  return `${i?.path.map(String).join('.') || 'document'}: ${i?.message}`;
}

function loadDoc(ref: string): LoadedDoc | string {
  const all = files();
  if (ref === 'shell') {
    const app = appSchema.safeParse(json(all[APP_JSON]));
    if (!app.success) return `${APP_JSON} cannot be read: ${issue(app.error)}`;
    return {
      path: APP_JSON,
      text: all[APP_JSON],
      root: app.data.shell ?? defaultShell(),
      build: (shell) => {
        const next = appSchema.safeParse({ ...app.data, shell });
        return next.success ? { ok: true, text: serializeDoc(next.data) } : { ok: false, error: issue(next.error) };
      },
    };
  }
  const page = /^page:([a-z0-9][a-z0-9-]*)$/.exec(ref);
  if (page) {
    const path = pagePath(page[1]!);
    const parsed = pageSchema.safeParse(json(all[path]));
    if (!parsed.success) return all[path] === undefined ? `There is no page “${page[1]}”.` : `${path} cannot be read: ${issue(parsed.error)}`;
    return {
      path,
      text: all[path],
      root: parsed.data.root,
      build: (root) => {
        const next = pageSchema.safeParse({ ...parsed.data, root });
        return next.success ? { ok: true, text: serializeDoc(next.data) } : { ok: false, error: issue(next.error) };
      },
    };
  }
  const component = /^component:([a-z0-9][a-z0-9-]*)@([1-9][0-9]*)$/.exec(ref);
  if (component) {
    const path = componentPath(component[1]!, Number(component[2]));
    const parsed = tenantComponentSchema.safeParse(json(all[path]));
    if (!parsed.success) return all[path] === undefined ? `There is no ${ref.slice('component:'.length)}.` : `${path} cannot be read: ${issue(parsed.error)}`;
    return {
      path,
      text: all[path],
      root: parsed.data.root,
      component: parsed.data,
      build: (root) => {
        const next = tenantComponentSchema.safeParse(pruneComponent({ ...parsed.data, root }));
        return next.success ? { ok: true, text: serializeDoc(next.data) } : { ok: false, error: issue(next.error) };
      },
    };
  }
  return `“${ref}” is not a document. Use page:<id>, shell, or component:<name>@<version> (see inspect_site).`;
}

/** How often a component is used outside its own files — of one version, when given. */
function usesOf(name: string, version?: number, all = files()): number {
  const type = tenantType(name);
  let count = 0;
  const visit = (root: Node | undefined, path: string) => {
    if (!root || componentFileOf(path)?.name === name) return;
    for (const n of walk(root)) if (n.type === type && (version === undefined || n.version === version)) count++;
  };
  for (const [path, text] of Object.entries(all)) {
    const doc = json(text) as { root?: Node; shell?: Node } | undefined;
    if (pageIdFromPath(path) || componentFileOf(path)) visit(doc?.root, path);
    else if (path === APP_JSON) visit(doc?.shell, path);
  }
  return count;
}

/**
 * Write a document through the run's transaction, after checking every node's placement. A
 * version of a component that pages already use is never edited in place.
 */
function save(ctx: TenantToolContext, doc: LoadedDoc, root: Node, registry: Registry, summary: string): ToolOutcome {
  if (doc.component && usesOf(doc.component.name, doc.component.version) > 0) {
    return err(`Version ${doc.component.version} of ${doc.component.name} is used by pages, so it is not edited in place. Call start_component_version and edit the new version.`);
  }
  const placement = checkPlacement(root, registry);
  if (placement) return err(placement);
  const built = doc.build(root);
  if (!built.ok) return err(`That would not be a valid document — ${built.error}`);
  if (built.text === doc.text) return { content: 'Nothing changed.' };
  const outcome = doc.text === undefined ? ctx.tx.create(doc.path, built.text) : ctx.tx.patch(doc.path, { oldText: doc.text, newText: built.text });
  if (outcome.isError) return outcome;
  return { ...outcome, content: summary };
}

function checkPlacement(root: Node, registry: Registry): string | null {
  for (const node of walk(root)) {
    const def = definitionFor(registry, node);
    if (!def) return `“${node.type}” is not a known component. list_component_types lists what exists.`;
    for (const [slot, children] of Object.entries(node.slots ?? {})) {
      for (const [index, child] of children.entries()) {
        const placement = canPlace(registry, node.type, slot, child.type, index);
        if (!placement.ok && registry.has(child.type)) return `${placement.reason} (node ${child.id} in ${node.id}.${slot})`;
      }
    }
  }
  return null;
}

interface Found {
  node: Node;
  parent: Node | null;
  slot: string | null;
  index: number;
}

function find(root: Node, id: string, parent: Node | null = null, slot: string | null = null, index = 0): Found | null {
  if (root.id === id) return { node: root, parent, slot, index };
  for (const [name, children] of Object.entries(root.slots ?? {})) {
    for (const [i, child] of children.entries()) {
      const hit = find(child, id, root, name, i);
      if (hit) return hit;
    }
  }
  return null;
}

/** A tree edited in place: the copy is the agent's to mutate, the original stays as loaded. */
function edit(root: Node, change: (copy: Node) => string | null): { root: Node } | { error: string } {
  const copy = structuredClone(root) as Node;
  const problem = change(copy);
  return problem ? { error: problem } : { root: copy };
}

/** Marks an id given only so the schema can check a model-written node; always replaced. */
const PENDING_ID = '__pending_';

/** Fresh ids for a node from the model, keeping any it gave that are free. */
function withIds(node: Node, taken: Set<string>): Node {
  const copy = structuredClone(node) as Node;
  for (const n of walk(copy)) {
    if (!n.id || n.id.startsWith(PENDING_ID) || taken.has(n.id)) n.id = newNodeId(taken);
    taken.add(n.id);
  }
  return copy;
}

function nodeArg(input: Record<string, unknown>): Node | string {
  const raw = input.node as Record<string, unknown> | undefined;
  if (!raw || typeof raw !== 'object') return '`node` must be an object: { type, props?, slots?, bind?, action? }.';
  const parsed = nodeSchema.safeParse(stampIds(raw));
  return parsed.success ? parsed.data : `node: ${issue(parsed.error)}`;
}

/** Give every node in a model-written tree an id, so the schema checks the rest of it. */
function stampIds(raw: Record<string, unknown>): Record<string, unknown> {
  const copy: Record<string, unknown> = { ...raw, id: typeof raw.id === 'string' ? raw.id : `${PENDING_ID}${Math.random().toString(36).slice(2, 10)}` };
  if (copy.slots && typeof copy.slots === 'object') {
    copy.slots = Object.fromEntries(
      Object.entries(copy.slots as Record<string, unknown[]>).map(([k, v]) => [k, Array.isArray(v) ? v.map((c) => stampIds(c as Record<string, unknown>)) : v]),
    );
  }
  return copy;
}

const str = (input: Record<string, unknown>, key: string): string => (typeof input[key] === 'string' ? (input[key] as string) : '');

const DOC = { type: 'string', description: 'page:<id>, shell, or component:<name>@<version>' };

// ---------------------------------------------------------------------------
// Compact views for the model
// ---------------------------------------------------------------------------

function compact(node: Node): unknown {
  const out: Record<string, unknown> = { id: node.id, type: node.type };
  if (node.version) out.version = node.version;
  if (node.props && Object.keys(node.props).length) out.props = node.props;
  if (node.responsive) out.responsive = node.responsive;
  if (node.bind) out.bind = node.bind;
  if (node.action) out.action = node.action;
  if (node.slots) out.slots = Object.fromEntries(Object.entries(node.slots).map(([k, v]) => [k, v.map(compact)]));
  return out;
}

function readApp(): App | null {
  const app = appSchema.safeParse(json(files()[APP_JSON]));
  return app.success ? app.data : null;
}

// ---------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------

export const VISUAL_TOOLS: Tool[] = [
  {
    name: 'inspect_site',
    description:
      'CALL THIS FIRST. The site at a glance: routes and the page each shows (and which are detail pages of what content), the menus, whether there is an app shell, every page, the site’s own components with their versions, and how many problems check_visual_site currently reports. Cheap; use inspect_document for one tree.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    describe: () => 'inspect site',
    maxResultChars: 12_000,
    run: async () => {
      const all = files();
      const app = readApp();
      const pages = Object.keys(all)
        .map(pageIdFromPath)
        .filter((id): id is string => id !== null)
        .map((id) => {
          const p = pageSchema.safeParse(json(all[pagePath(id)]));
          return p.success
            ? { doc: `page:${id}`, title: p.data.title, detailOf: p.data.data ? `${p.data.data.source.instance}/${p.data.data.source.contentType}` : undefined }
            : { doc: `page:${id}`, unreadable: true };
        });
      const components = new Map<string, number[]>();
      for (const path of Object.keys(all)) {
        const f = componentFileOf(path);
        if (f) components.set(f.name, [...(components.get(f.name) ?? []), f.version].sort((a, b) => a - b));
      }
      const problems = checkVisualSite(all, registryFromFiles(all), useVisual.getState().contentSchema);
      return {
        content: JSON.stringify(
          {
            routes: app?.routes ?? 'app.json cannot be read',
            menus: app?.navigation ?? {},
            shell: app?.shell ? 'yes — edit with doc "shell"' : 'none yet — doc "shell" starts from the default (menu + page content)',
            seo: app?.seo,
            pages,
            components: [...components].map(([name, versions]) => ({
              type: tenantType(name),
              versions,
              docs: versions.map((v) => `component:${name}@${v}`),
              usedTimes: usesOf(name, undefined, all),
            })),
            problems: { errors: problems.filter((p) => p.severity === 'error').length, warnings: problems.filter((p) => p.severity === 'warning').length },
          },
          null,
          1,
        ),
      };
    },
  },
  {
    name: 'inspect_document',
    description:
      'One document’s node tree, compactly: every node’s id, type, props, bindings, action and slots. Read the document before editing it — every edit tool addresses nodes by these ids.',
    input_schema: { type: 'object', properties: { doc: DOC }, required: ['doc'], additionalProperties: false },
    describe: (input) => `inspect ${str(input, 'doc')}`,
    maxResultChars: 16_000,
    run: async (input) => {
      const doc = loadDoc(str(input, 'doc'));
      if (typeof doc === 'string') return err(doc);
      const extra = doc.component
        ? { component: { label: doc.component.label, props: doc.component.props, slots: doc.component.slots, bindings: doc.component.bindings, slotTargets: doc.component.slotTargets } }
        : {};
      return { content: JSON.stringify({ ...extra, root: compact(doc.root) }) };
    },
  },
  {
    name: 'list_component_types',
    description:
      'Every component type you may place: its props (with kinds, options and defaults; `responsive` ones may differ on tablet/mobile), its slots and what each accepts, the actions it can run, and where it may go. The site’s own components (tenant.*) are included. Use the exact type names and prop names from here.',
    input_schema: {
      type: 'object',
      properties: { category: { type: 'string', description: 'Only this category, e.g. Layout, Content, Data, Forms' } },
      additionalProperties: false,
    },
    describe: () => 'list component types',
    maxResultChars: 20_000,
    run: async (input) => {
      const category = str(input, 'category').toLowerCase();
      const out = [...registryFromFiles().values()]
        .filter((d) => d.draggable !== false)
        .filter((d) => !category || d.category.toLowerCase() === category)
        .map((d) => ({
          type: d.type,
          version: d.template ? d.version : undefined,
          label: d.label,
          category: d.category,
          description: d.description,
          props: d.props.map((p) => ({
            name: p.name,
            kind: p.kind,
            ...('options' in p ? { options: p.options.map((o) => o.value) } : {}),
            ...('default' in p && p.default !== undefined ? { default: p.default } : {}),
            ...(p.responsive ? { responsive: true } : {}),
            ...(p.required ? { required: true } : {}),
          })),
          slots: d.slots?.map((s) => ({ name: s.name, accepts: s.allowed ?? 'any', max: s.max })),
          actions: d.actions,
          allowedParents: d.allowedParents,
        }));
      return { content: JSON.stringify(out) };
    },
  },
  {
    name: 'insert_node',
    description:
      'Insert a component (with any children) into a slot of a node. `node` is { type, props?, slots?: { name: [nodes] }, bind?, action? }; ids are generated. Refused, with the reason, if any node may not go where it is placed. Prefer building a whole section in one call over many small inserts.',
    input_schema: {
      type: 'object',
      properties: {
        doc: DOC,
        parent: { type: 'string', description: 'id of the node to insert into; omit for the document’s root' },
        slot: { type: 'string', description: 'its slot, e.g. "default", "item"; omit for "default"' },
        index: { type: 'number', description: 'position in the slot; omit to append' },
        node: { type: 'object', description: '{ type, props?, slots?, bind?, action? }' },
      },
      required: ['doc', 'node'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Insert a ${String((input.node as { type?: string })?.type ?? 'component')} into ${str(input, 'doc')}`,
    describe: (input) => `inserted ${String((input.node as { type?: string })?.type ?? 'a component')} into ${str(input, 'doc')}`,
    run: async (input, ctx) => {
      const doc = loadDoc(str(input, 'doc'));
      if (typeof doc === 'string') return err(doc);
      const parsed = nodeArg(input);
      if (typeof parsed === 'string') return err(parsed);
      const registry = registryFromFiles();
      const def = registry.get(parsed.type);
      if (def?.template && parsed.version === undefined) parsed.version = def.version;
      let inserted = '';
      const result = edit(doc.root, (root) => {
        const target = find(root, str(input, 'parent') || root.id);
        if (!target) return `There is no node “${str(input, 'parent')}” in ${str(input, 'doc')}.`;
        const slot = str(input, 'slot') || 'default';
        const list = (target.node.slots ??= {})[slot] ?? [];
        const index = typeof input.index === 'number' ? Math.max(0, Math.min(list.length, Math.round(input.index))) : list.length;
        const placement = canPlace(registry, target.node.type, slot, parsed.type, list.length);
        if (!placement.ok) return placement.reason;
        const node = withIds(parsed, new Set([...walk(root)].map((n) => n.id)));
        inserted = node.id;
        target.node.slots[slot] = [...list.slice(0, index), node, ...list.slice(index)];
        return null;
      });
      if ('error' in result) return err(result.error);
      return save(ctx, doc, result.root, registry, `Inserted ${parsed.type} as ${inserted}.`);
    },
  },
  {
    name: 'move_node',
    description: 'Move a node (and everything in it) to another slot or position, in the same document. Refused if it may not go there.',
    input_schema: {
      type: 'object',
      properties: { doc: DOC, node: { type: 'string' }, parent: { type: 'string' }, slot: { type: 'string' }, index: { type: 'number' } },
      required: ['doc', 'node', 'parent', 'slot'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Move ${str(input, 'node')} in ${str(input, 'doc')}`,
    describe: (input) => `moved ${str(input, 'node')}`,
    run: async (input, ctx) => {
      const doc = loadDoc(str(input, 'doc'));
      if (typeof doc === 'string') return err(doc);
      const registry = registryFromFiles();
      const result = edit(doc.root, (root) => {
        const from = find(root, str(input, 'node'));
        if (!from?.parent || !from.slot) return `There is no movable node “${str(input, 'node')}”.`;
        if (find(from.node, str(input, 'parent'))) return 'A node cannot be moved into itself.';
        from.parent.slots![from.slot]!.splice(from.index, 1);
        const target = find(root, str(input, 'parent'));
        if (!target) return `There is no node “${str(input, 'parent')}”.`;
        const slot = str(input, 'slot');
        const list = (target.node.slots ??= {})[slot] ?? [];
        const placement = canPlace(registry, target.node.type, slot, from.node.type, list.length);
        if (!placement.ok) return placement.reason;
        const index = typeof input.index === 'number' ? Math.max(0, Math.min(list.length, Math.round(input.index))) : list.length;
        target.node.slots[slot] = [...list.slice(0, index), from.node, ...list.slice(index)];
        return null;
      });
      if ('error' in result) return err(result.error);
      return save(ctx, doc, result.root, registry, `Moved ${str(input, 'node')}.`);
    },
  },
  {
    name: 'remove_node',
    description: 'Remove a node and everything in it from a document. The page root and the shell’s page content cannot be removed.',
    input_schema: { type: 'object', properties: { doc: DOC, node: { type: 'string' } }, required: ['doc', 'node'], additionalProperties: false },
    risk: 'safe',
    summarize: (input) => `Remove ${str(input, 'node')} from ${str(input, 'doc')}`,
    describe: (input) => `removed ${str(input, 'node')}`,
    run: async (input, ctx) => {
      const doc = loadDoc(str(input, 'doc'));
      if (typeof doc === 'string') return err(doc);
      const result = edit(doc.root, (root) => {
        const hit = find(root, str(input, 'node'));
        if (!hit?.parent || !hit.slot) return `There is no removable node “${str(input, 'node')}”.`;
        if (hit.node.type === 'dcms.outlet') return 'The page content in the shell cannot be removed: every page renders there.';
        hit.parent.slots![hit.slot]!.splice(hit.index, 1);
        return null;
      });
      if ('error' in result) return err(result.error);
      return save(ctx, doc, result.root, registryFromFiles(), `Removed ${str(input, 'node')}.`);
    },
  },
  {
    name: 'duplicate_node',
    description: 'Copy a node (with fresh ids for it and everything in it) right after the original.',
    input_schema: { type: 'object', properties: { doc: DOC, node: { type: 'string' } }, required: ['doc', 'node'], additionalProperties: false },
    risk: 'safe',
    summarize: (input) => `Duplicate ${str(input, 'node')}`,
    describe: (input) => `duplicated ${str(input, 'node')}`,
    run: async (input, ctx) => {
      const doc = loadDoc(str(input, 'doc'));
      if (typeof doc === 'string') return err(doc);
      let copyId = '';
      const result = edit(doc.root, (root) => {
        const hit = find(root, str(input, 'node'));
        if (!hit?.parent || !hit.slot) return `There is no node “${str(input, 'node')}” to copy.`;
        const copy = withIds({ ...hit.node, id: '' }, new Set([...walk(root)].map((n) => n.id)));
        copyId = copy.id;
        hit.parent.slots![hit.slot]!.splice(hit.index + 1, 0, copy);
        return null;
      });
      if ('error' in result) return err(result.error);
      return save(ctx, doc, result.root, registryFromFiles(), `Duplicated as ${copyId}.`);
    },
  },
  {
    name: 'set_props',
    description:
      'Set props of a node. `props` maps prop name → value (null removes it). With `device` "tablet" or "mobile" the values are overrides for that size and smaller, and only for props list_component_types marks responsive. Values must be allowed by the prop (options, kinds) — wrong ones are refused with the reason.',
    input_schema: {
      type: 'object',
      properties: {
        doc: DOC,
        node: { type: 'string' },
        props: { type: 'object' },
        device: { type: 'string', enum: ['tablet', 'mobile'] },
      },
      required: ['doc', 'node', 'props'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Change ${Object.keys((input.props as object) ?? {}).join(', ')} of ${str(input, 'node')}`,
    describe: (input) => `set ${Object.keys((input.props as object) ?? {}).join(', ')} on ${str(input, 'node')}`,
    run: async (input, ctx) => {
      const doc = loadDoc(str(input, 'doc'));
      if (typeof doc === 'string') return err(doc);
      const registry = registryFromFiles();
      const device = str(input, 'device') as 'tablet' | 'mobile' | '';
      const changes = (input.props ?? {}) as Record<string, unknown>;
      const result = edit(doc.root, (root) => {
        const hit = find(root, str(input, 'node'));
        if (!hit) return `There is no node “${str(input, 'node')}”.`;
        const def = definitionFor(registry, hit.node);
        if (!def) return `“${hit.node.type}” is not a known component.`;
        const target: Record<string, unknown> = device ? { ...hit.node.responsive?.[device] } : { ...hit.node.props };
        for (const [name, value] of Object.entries(changes)) {
          const prop = def.props.find((p) => p.name === name);
          if (!prop) return `${def.label} has no prop “${name}”. Its props: ${def.props.map((p) => p.name).join(', ') || 'none'}.`;
          if (device && !prop.responsive) return `${def.label} › ${name} cannot differ on ${device}.`;
          if (value === null) delete target[name];
          else target[name] = value;
        }
        if (device) {
          hit.node.responsive = { ...hit.node.responsive, [device]: target };
          if (!Object.keys(target).length) delete hit.node.responsive[device];
          if (!Object.keys(hit.node.responsive).length) delete hit.node.responsive;
        } else {
          hit.node.props = target;
          if (!Object.keys(target).length) delete hit.node.props;
        }
        return null;
      });
      if ('error' in result) return err(result.error);
      // Values are checked by the site validator's rules, so a wrong option is named, not saved.
      const built = doc.build(result.root);
      if (!built.ok) return err(`That would not be a valid document — ${built.error}`);
      const problems = checkVisualSite({ ...files(), [doc.path]: built.text }, registry, useVisual.getState().contentSchema).filter(
        (p) => p.file === doc.path && p.nodeId === str(input, 'node') && p.severity === 'error',
      );
      if (problems.length) return err(problems.map((p) => p.message).join('\n'));
      return save(ctx, doc, result.root, registry, `Updated ${str(input, 'node')}.`);
    },
  },
  {
    name: 'bind_props',
    description:
      'Make props of a node show a field of the item in scope instead of a fixed value — inside a Collection’s "item" slot, or anywhere on a detail page. `bind` maps prop → field path (`title`, `values.colour`, `#slug`, `#number`); null unbinds. Use describe_content_types for the real field names; check_visual_site reports fields that do not exist.',
    input_schema: {
      type: 'object',
      properties: { doc: DOC, node: { type: 'string' }, bind: { type: 'object' } },
      required: ['doc', 'node', 'bind'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Bind ${Object.keys((input.bind as object) ?? {}).join(', ')} of ${str(input, 'node')} to content`,
    describe: (input) => `bound ${Object.keys((input.bind as object) ?? {}).join(', ')} on ${str(input, 'node')}`,
    run: async (input, ctx) => {
      const doc = loadDoc(str(input, 'doc'));
      if (typeof doc === 'string') return err(doc);
      const registry = registryFromFiles();
      const result = edit(doc.root, (root) => {
        const hit = find(root, str(input, 'node'));
        if (!hit) return `There is no node “${str(input, 'node')}”.`;
        const bind = { ...hit.node.bind };
        for (const [prop, path] of Object.entries((input.bind ?? {}) as Record<string, unknown>)) {
          if (path === null) delete bind[prop];
          else if (typeof path === 'string') bind[prop] = path;
          else return `The field path for “${prop}” must be a string or null.`;
        }
        hit.node.bind = bind;
        if (!Object.keys(bind).length) delete hit.node.bind;
        return null;
      });
      if ('error' in result) return err(result.error);
      return save(ctx, doc, result.root, registry, `Bound ${str(input, 'node')}.`);
    },
  },
  {
    name: 'set_action',
    description:
      'What a node does when clicked (only components whose list_component_types entry has actions): {type:"navigate", to:"/about"} (or "/events/:slug" inside an item), {type:"open-external", href, newTab?}, {type:"scroll-to", target:<node id>}, {type:"open-modal", modal:<Popup node id>}, {type:"show-toast", message}. null removes it.',
    input_schema: {
      type: 'object',
      properties: { doc: DOC, node: { type: 'string' }, action: { type: ['object', 'null'] } },
      required: ['doc', 'node', 'action'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Set what ${str(input, 'node')} does when clicked`,
    describe: (input) => `set action on ${str(input, 'node')}`,
    run: async (input, ctx) => {
      const doc = loadDoc(str(input, 'doc'));
      if (typeof doc === 'string') return err(doc);
      const registry = registryFromFiles();
      const action = input.action === null ? null : actionSchema.safeParse(input.action);
      if (action && !action.success) return err(`action: ${issue(action.error)}`);
      const result = edit(doc.root, (root) => {
        const hit = find(root, str(input, 'node'));
        if (!hit) return `There is no node “${str(input, 'node')}”.`;
        const def = definitionFor(registry, hit.node);
        if (action && !def?.actions?.includes(action.data.type)) {
          return `${def?.label ?? hit.node.type} does not run “${action.data.type}” actions (it runs: ${def?.actions?.join(', ') || 'none'}).`;
        }
        if (action) hit.node.action = action.data;
        else delete hit.node.action;
        return null;
      });
      if ('error' in result) return err(result.error);
      return save(ctx, doc, result.root, registry, `Updated the action of ${str(input, 'node')}.`);
    },
  },
  {
    name: 'create_page',
    description:
      'Add a page and the route that shows it, optionally with a main-menu entry. For a detail page (one item of plugin content, e.g. /events/:slug) pass `detail_of` { instance, contentType } and a path with :slug. The new page is empty: fill it with insert_node on doc page:<id>.',
    input_schema: {
      type: 'object',
      properties: {
        title: { type: 'string' },
        path: { type: 'string', description: '/about, /events/:slug' },
        in_menu: { type: 'boolean' },
        detail_of: { type: 'object', description: '{ instance, contentType }' },
      },
      required: ['title', 'path'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Add the page “${str(input, 'title')}” at ${str(input, 'path')}`,
    describe: (input) => `created page ${str(input, 'title')}`,
    run: async (input, ctx) => {
      const app = readApp();
      if (!app) return err(`${APP_JSON} cannot be read.`);
      const title = str(input, 'title').trim();
      const path = str(input, 'path').trim();
      const taken = new Set(Object.keys(files()).map(pageIdFromPath).filter((id): id is string => id !== null));
      const id = pageIdFor(title, taken);
      let data: Page['data'];
      if (input.detail_of) {
        const source = sourceSchema.safeParse(input.detail_of);
        const param = /:([A-Za-z][A-Za-z0-9]*)/.exec(path)?.[1];
        if (!source.success) return err(`detail_of: ${issue(source.error)}`);
        if (!param) return err('A detail page needs a parameter in its path, like /events/:slug.');
        data = { source: source.data, param };
      }
      const routeIds = new Set(app.routes.map((r) => r.id));
      let routeId = id;
      for (let n = 2; routeIds.has(routeId); n++) routeId = `${id}-${n}`;
      const nextApp = appSchema.safeParse({
        ...app,
        routes: [...app.routes, { id: routeId, path, page: id }],
        navigation: input.in_menu ? { ...app.navigation, main: [...(app.navigation?.main ?? []), { label: title, to: path }] } : app.navigation,
      });
      if (!nextApp.success) return err(issue(nextApp.error));
      // A detail page's tab and share title is the item's own, until someone says otherwise.
      const page = pageSchema.safeParse({ schemaVersion: 1, id, title, ...(data ? { data, seo: { title: '{title}' } } : {}), root: { id: newNodeId(), type: 'dcms.page', slots: { default: [] } } });
      if (!page.success) return err(issue(page.error));
      const created = ctx.tx.create(pagePath(id), serializeDoc(page.data));
      if (created.isError) return created;
      const routed = ctx.tx.patch(APP_JSON, { oldText: files()[APP_JSON]!, newText: serializeDoc(nextApp.data) });
      if (routed.isError) return routed;
      return { content: `Created page:${id} at ${path} (root ${page.data.root.id}).`, paths: [pagePath(id), APP_JSON] };
    },
  },
  {
    name: 'update_page',
    description: 'Change a page’s title, SEO ({ title?, description?, ogImage?, noIndex? } — on a detail page "{title}" quotes the item) or its address (menu entries follow).',
    input_schema: {
      type: 'object',
      properties: { page: { type: 'string' }, title: { type: 'string' }, seo: { type: 'object' }, path: { type: 'string' } },
      required: ['page'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Update the page ${str(input, 'page')}`,
    describe: (input) => `updated page ${str(input, 'page')}`,
    run: async (input, ctx) => {
      const id = str(input, 'page').replace(/^page:/, '');
      const path = pagePath(id);
      const page = pageSchema.safeParse(json(files()[path]));
      if (!page.success) return err(`There is no readable page “${id}”.`);
      const next = pageSchema.safeParse({
        ...page.data,
        ...(input.title ? { title: str(input, 'title') } : {}),
        ...(input.seo ? { seo: { ...page.data.seo, ...(input.seo as object) } } : {}),
      });
      if (!next.success) return err(issue(next.error));
      const paths: string[] = [];
      if (serializeDoc(next.data) !== files()[path]) {
        const o = ctx.tx.patch(path, { oldText: files()[path]!, newText: serializeDoc(next.data) });
        if (o.isError) return o;
        paths.push(path);
      }
      if (input.path) {
        const app = readApp();
        const route = app?.routes.find((r) => r.page === id);
        if (!app || !route) return err('That page has no route to change.');
        const nextApp = appSchema.safeParse({
          ...app,
          routes: app.routes.map((r) => (r.page === id ? { ...r, path: str(input, 'path') } : r)),
          navigation: app.navigation
            ? Object.fromEntries(Object.entries(app.navigation).map(([m, items]) => [m, items.map((i) => (i.to === route.path ? { ...i, to: str(input, 'path') } : i))]))
            : undefined,
        });
        if (!nextApp.success) return err(issue(nextApp.error));
        const o = ctx.tx.patch(APP_JSON, { oldText: files()[APP_JSON]!, newText: serializeDoc(nextApp.data) });
        if (o.isError) return o;
        paths.push(APP_JSON);
      }
      return { content: paths.length ? `Updated ${id}.` : 'Nothing changed.', paths };
    },
  },
  {
    name: 'delete_page',
    description: 'Delete a page, its routes and the menu entries pointing at them. The home page (/) cannot be deleted.',
    input_schema: { type: 'object', properties: { page: { type: 'string' } }, required: ['page'], additionalProperties: false },
    risk: 'dangerous',
    summarize: (input) => `Delete the page ${str(input, 'page')}`,
    describe: (input) => `deleted page ${str(input, 'page')}`,
    run: async (input, ctx) => {
      const id = str(input, 'page').replace(/^page:/, '');
      const app = readApp();
      if (!app) return err(`${APP_JSON} cannot be read.`);
      const removed = app.routes.filter((r) => r.page === id);
      if (removed.some((r) => r.path === '/')) return err('The home page cannot be deleted.');
      const paths = new Set(removed.map((r) => r.path));
      const nextApp = appSchema.safeParse({
        ...app,
        routes: app.routes.filter((r) => r.page !== id),
        navigation: app.navigation ? Object.fromEntries(Object.entries(app.navigation).map(([m, items]) => [m, items.filter((i) => !paths.has(i.to))])) : undefined,
      });
      if (!nextApp.success) return err(issue(nextApp.error));
      const o = ctx.tx.patch(APP_JSON, { oldText: files()[APP_JSON]!, newText: serializeDoc(nextApp.data) });
      if (o.isError) return o;
      const r = ctx.tx.remove(pagePath(id));
      if (r.isError) return r;
      return { content: `Deleted ${id}.`, paths: [APP_JSON, pagePath(id)] };
    },
  },
  {
    name: 'create_component',
    description:
      'Make a reusable component (v1) from a node tree — e.g. an event card. Then edit doc component:<name>@1 with the node tools, and expose what pages may change with expose_setting / expose_slot. Placed with insert_node as { type: "tenant.<name>" }.',
    input_schema: {
      type: 'object',
      properties: { label: { type: 'string' }, root: { type: 'object', description: 'the template tree: { type, props?, slots? }' } },
      required: ['label', 'root'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Create the component “${str(input, 'label')}”`,
    describe: (input) => `created component ${str(input, 'label')}`,
    run: async (input, ctx) => {
      const parsed = nodeArg({ node: input.root });
      if (typeof parsed === 'string') return err(parsed.replace(/^node/, 'root'));
      const taken = new Set(Object.keys(files()).map(componentFileOf).filter(Boolean).map((f) => f!.name));
      const name = pageIdFor(str(input, 'label'), taken).slice(0, 48);
      const root = withIds(parsed, new Set());
      const placement = checkPlacement(root, registryFromFiles());
      if (placement) return err(placement);
      const doc = tenantComponentSchema.safeParse({ schemaVersion: 1, name, version: 1, label: str(input, 'label'), props: [], slots: [], bindings: {}, slotTargets: {}, root });
      if (!doc.success) return err(issue(doc.error));
      const o = ctx.tx.create(componentPath(name, 1), serializeDoc(doc.data));
      if (o.isError) return o;
      return { ...o, content: `Created ${tenantType(name)} v1 (root ${root.id}) — edit it as component:${name}@1.` };
    },
  },
  {
    name: 'start_component_version',
    description: 'Start the next version of a component as a copy of its latest. Needed before editing a component that pages already use; pages move to it with update_instances.',
    input_schema: { type: 'object', properties: { name: { type: 'string' } }, required: ['name'], additionalProperties: false },
    risk: 'safe',
    summarize: (input) => `Start a new version of ${str(input, 'name')}`,
    describe: (input) => `started a new version of ${str(input, 'name')}`,
    run: async (input, ctx) => {
      const name = str(input, 'name').replace(/^tenant\./, '');
      const versions = Object.keys(files()).map(componentFileOf).filter((f) => f?.name === name).map((f) => f!.version);
      if (!versions.length) return err(`There is no component “${name}”.`);
      const latest = Math.max(...versions);
      const doc = tenantComponentSchema.safeParse(json(files()[componentPath(name, latest)]));
      if (!doc.success) return err(`${componentPath(name, latest)} cannot be read.`);
      const o = ctx.tx.create(componentPath(name, latest + 1), serializeDoc({ ...doc.data, version: latest + 1 }));
      if (o.isError) return o;
      return { ...o, content: `Started component:${name}@${latest + 1}.` };
    },
  },
  {
    name: 'expose_setting',
    description:
      'Let pages change a prop of a node inside a component: it becomes a setting of the component, defaulting to the node’s current value. Pass `link: true` instead of `prop` to expose a button’s link as a url setting.',
    input_schema: {
      type: 'object',
      properties: {
        component: { type: 'string', description: 'component:<name>@<version>' },
        node: { type: 'string' },
        prop: { type: 'string' },
        link: { type: 'boolean' },
        name: { type: 'string', description: 'the setting’s name; defaults to the prop’s' },
        label: { type: 'string' },
      },
      required: ['component', 'node'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Expose ${input.link ? 'the link' : str(input, 'prop')} of ${str(input, 'node')}`,
    describe: (input) => `exposed ${input.link ? 'link' : str(input, 'prop')} of ${str(input, 'node')}`,
    run: async (input, ctx) => {
      const doc = loadDoc(str(input, 'component'));
      if (typeof doc === 'string' || !doc.component) return err(typeof doc === 'string' ? doc : 'Not a component.');
      const hit = find(doc.root, str(input, 'node'));
      if (!hit) return err(`There is no node “${str(input, 'node')}” in it.`);
      const registry = registryFromFiles();
      const def = definitionFor(registry, hit.node);
      const comp = doc.component;
      const taken = new Set(comp.props.map((p) => p.name));
      let next: TenantComponentDoc;
      if (input.link) {
        if (!def?.actions?.includes('navigate')) return err(`${def?.label ?? hit.node.type} has no link.`);
        let name = str(input, 'name') || 'link';
        for (let n = 2; taken.has(name); n++) name = `link${n}`;
        next = { ...comp, props: [...comp.props, { kind: 'url', name, label: str(input, 'label') || 'Link' }], bindings: { ...comp.bindings, [name]: [{ node: hit.node.id, action: 'link' }] } };
      } else {
        const prop = def?.props.find((p) => p.name === str(input, 'prop'));
        if (!prop) return err(`${def?.label ?? hit.node.type} has no prop “${str(input, 'prop')}”.`);
        let name = str(input, 'name') || prop.name;
        for (let n = 2; taken.has(name); n++) name = `${prop.name}${n}`;
        const current = hit.node.props?.[prop.name];
        const exposed = { ...prop, name, label: str(input, 'label') || `${def!.label}: ${prop.label}`, ...(current !== undefined && 'default' in prop ? { default: current } : {}) };
        next = { ...comp, props: [...comp.props, exposed as typeof prop], bindings: { ...comp.bindings, [name]: [{ node: hit.node.id, prop: prop.name }] } };
      }
      const parsed = tenantComponentSchema.safeParse(next);
      if (!parsed.success) return err(issue(parsed.error));
      const o = ctx.tx.patch(doc.path, { oldText: doc.text!, newText: serializeDoc(parsed.data) });
      return o.isError ? o : { ...o, content: `Exposed as the setting “${parsed.data.props.at(-1)!.name}”.` };
    },
  },
  {
    name: 'expose_slot',
    description: 'Let pages put their own content into an EMPTY slot of a node inside a component — e.g. the extras area of a card. Becomes a slot of the component.',
    input_schema: {
      type: 'object',
      properties: { component: { type: 'string' }, node: { type: 'string' }, slot: { type: 'string' }, name: { type: 'string' }, label: { type: 'string' } },
      required: ['component', 'node', 'slot'],
      additionalProperties: false,
    },
    risk: 'safe',
    summarize: (input) => `Expose the slot ${str(input, 'slot')} of ${str(input, 'node')}`,
    describe: (input) => `exposed slot ${str(input, 'slot')} of ${str(input, 'node')}`,
    run: async (input, ctx) => {
      const doc = loadDoc(str(input, 'component'));
      if (typeof doc === 'string' || !doc.component) return err(typeof doc === 'string' ? doc : 'Not a component.');
      const comp = doc.component;
      let name = str(input, 'name') || (str(input, 'slot') === 'default' ? 'content' : str(input, 'slot'));
      for (let n = 2; comp.slots.some((s) => s.name === name); n++) name = `${name}${n}`;
      const parsed = tenantComponentSchema.safeParse({
        ...comp,
        slots: [...comp.slots, { name, label: str(input, 'label') || name }],
        slotTargets: { ...comp.slotTargets, [name]: { node: str(input, 'node'), slot: str(input, 'slot') } },
      });
      if (!parsed.success) return err(issue(parsed.error));
      const o = ctx.tx.patch(doc.path, { oldText: doc.text!, newText: serializeDoc(parsed.data) });
      return o.isError ? o : { ...o, content: `Exposed as the slot “${name}”.` };
    },
  },
  {
    name: 'update_instances',
    description: 'Move every use of a component, on every page, the shell and in other components, to its latest version. Settings the latest version no longer has are dropped and named.',
    input_schema: { type: 'object', properties: { name: { type: 'string' } }, required: ['name'], additionalProperties: false },
    risk: 'safe',
    summarize: (input) => `Update every use of ${str(input, 'name')} to its latest version`,
    describe: (input) => `updated uses of ${str(input, 'name')}`,
    run: async (input, ctx) => {
      const name = str(input, 'name').replace(/^tenant\./, '');
      const versions = Object.keys(files()).map(componentFileOf).filter((f) => f?.name === name).map((f) => f!.version);
      if (!versions.length) return err(`There is no component “${name}”.`);
      const latest = tenantComponentSchema.safeParse(json(files()[componentPath(name, Math.max(...versions))]));
      if (!latest.success) return err('Its latest version cannot be read.');
      const type = tenantType(name);
      let updated = 0;
      const dropped = new Set<string>();
      const rewrite = (n: Node): Node => {
        let out = n;
        if (n.type === type && (n.version ?? latest.data.version) !== latest.data.version) {
          const m = migrateInstance(n, latest.data);
          m.dropped.forEach((d) => dropped.add(d));
          out = m.node;
          updated++;
        }
        return out.slots ? { ...out, slots: Object.fromEntries(Object.entries(out.slots).map(([k, v]) => [k, v.map(rewrite)])) } : out;
      };
      const paths: string[] = [];
      for (const ref of [...Object.keys(files()).map((p) => (pageIdFromPath(p) ? `page:${pageIdFromPath(p)}` : null)), 'shell']) {
        if (!ref) continue;
        const doc = loadDoc(ref);
        if (typeof doc === 'string' || (ref === 'shell' && doc.text === undefined)) continue;
        const before = updated;
        const root = rewrite(doc.root);
        if (updated === before) continue;
        const built = doc.build(root);
        if (!built.ok) return err(built.error);
        const o = ctx.tx.patch(doc.path, { oldText: doc.text!, newText: built.text });
        if (o.isError) return o;
        paths.push(doc.path);
      }
      return { content: `Updated ${updated} use(s)${dropped.size ? `; dropped settings: ${[...dropped].join(', ')}` : ''}.`, paths };
    },
  },
  {
    name: 'set_design_kit',
    description: `Set the whole site’s look — colours, type, spacing, corners, shadows — from a design kit. A kit is a complete token set; never write dcms/theme.json by hand. Kits: ${DESIGN_KITS.map((k) => `${k.id} (${k.description})`).join('; ')}.`,
    input_schema: { type: 'object', properties: { kit: { type: 'string', enum: DESIGN_KITS.map((k) => k.id) } }, required: ['kit'], additionalProperties: false },
    risk: 'safe',
    summarize: (input) => `Restyle the site with the “${findKit(str(input, 'kit'))?.name ?? str(input, 'kit')}” design kit`,
    describe: (input) => `applied design kit ${str(input, 'kit')}`,
    run: async (input, ctx) => {
      const kit = findKit(str(input, 'kit'));
      if (!kit) return err(`No kit named that. Available: ${DESIGN_KITS.map((k) => k.id).join(', ')}.`);
      const current = files()[THEME_JSON];
      const base = themeTokensSchema.safeParse(json(current));
      const next = serializeDoc(applyKit(base.success ? base.data : kit.theme, kit));
      if (next === current) return { content: `The site is already on “${kit.name}”.` };
      const o = current === undefined ? ctx.tx.create(THEME_JSON, next) : ctx.tx.patch(THEME_JSON, { oldText: current, newText: next });
      return o.isError ? o : { ...o, content: `Applied “${kit.name}”.` };
    },
  },
  {
    name: 'check_visual_site',
    description:
      'Check the whole site the way publishing does: every document parses, every component is where it may go, every prop value is allowed, bindings have an item and routes have pages. Free and certain — call it after writing, rather than reasoning about whether your own output was consistent.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    describe: () => 'check site',
    maxResultChars: 12_000,
    run: async () => {
      const problems = checkVisualSite(files(), registryFromFiles(), useVisual.getState().contentSchema);
      return { content: formatProblems(problems), isError: problems.some((p) => p.severity === 'error') };
    },
  },
];

/** The run's deterministic gate: the same report, errors fail it. */
export async function validateVisualSite(): Promise<{ ok: boolean; report: string }> {
  const problems = checkVisualSite(files(), registryFromFiles(), useVisual.getState().contentSchema);
  return { ok: !problems.some((p) => p.severity === 'error'), report: formatProblems(problems) };
}
