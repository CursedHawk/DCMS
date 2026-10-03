import { codeContractOf, codeContractPath, codeDefinitions, codeSourceOf, codeSourcePath, readCodeContracts, type CodeContract } from './code';
import { BUILTIN_COMPONENTS } from './components';
import { BINDABLE, isConnectionSource, sourceSchema, type Source } from './data';
import { appSchema, pageSchema, walk, type App, type NavItem, type Node, type Page } from './document';
import { isInternalPath } from './ids';
import { APP_JSON, THEME_JSON, pageIdFromPath } from './paths';
import { propValueSchema } from './props';
import { canPlace, type Registry } from './registry';
import { definitionFor } from './scope';
import { componentDependencies, componentFileOf, readComponentDocs, siteRegistry, tenantType } from './tenant';
import { themeTokensSchema } from './theme';

/**
 * Everything wrong with a Mode D site, from its file map alone.
 *
 * One function for every consumer — the builder's problem list, the AI agent's check tool, and
 * the check before publishing — so they cannot disagree about whether a site is valid. It asks
 * the same `canPlace` and the same prop schemas the editor does: a site the builder would not
 * let you make is a site this reports.
 *
 * Errors stop the site working as authored (a page that cannot render, a route to nowhere, a
 * component where it may not go). Warnings work but are probably not what was meant.
 */

export interface SiteProblem {
  severity: 'error' | 'warning';
  file: string;
  /** The node it is about, when there is one — what the builder selects when it is clicked. */
  nodeId?: string;
  message: string;
}

const OUTLET = 'dcms.outlet';
const PAGE_ROOT = 'dcms.page';

function parse(text: string | undefined): { ok: true; value: unknown } | { ok: false; message: string } {
  if (text === undefined) return { ok: false, message: 'is missing' };
  try {
    return { ok: true, value: JSON.parse(text) };
  } catch (e) {
    return { ok: false, message: `is not valid JSON: ${(e as Error).message}` };
  }
}

/** Whether a route serves an address, `:param` matching any one segment. */
function routeServes(route: string, address: string): boolean {
  const path = address.split(/[?#]/)[0]!;
  const left = route.split('/').filter(Boolean);
  const right = path.split('/').filter(Boolean);
  return left.length === right.length && left.every((part, i) => part.startsWith(':') || part === right[i]);
}

function navPaths(items: readonly NavItem[]): string[] {
  return items.flatMap((item) => [item.to, ...navPaths(item.children ?? [])]);
}

/**
 * What the tenant's plugins actually offer, when the caller knows it (the builder does; a bare
 * file map does not): `instance/contentType` → the field paths items of it have.
 */
/**
 * Source key → the field paths its items have. A set holding `*` accepts any field: an external
 * connection's items are whatever its API returns.
 */
export type ContentSchema = ReadonlyMap<string, ReadonlySet<string>>;

const META = new Set(['#slug', '#id', '#publishedAt', '#index', '#number', '#count']);

/** What bindings in a subtree read from: a known source, an item of unknown shape, or nothing. */
type Scope = { kind: 'none' } | { kind: 'unknown' } | { kind: 'source'; key: string };

/**
 * Page state: whatever a node shows `when`, or an action sets or toggles, is a value its page
 * declares — and a toggle flips a true/false one. The shell belongs to no page, so it has none.
 * (A component's template is checked where it is placed, which is not known here.)
 */
function checkState(root: Node, file: string, state: Readonly<Record<string, unknown>> | null, out: SiteProblem[]): void {
  for (const node of walk(root)) {
    const keys: { key: string; toggle: boolean }[] = [];
    if (node.when) keys.push({ key: node.when.state, toggle: false });
    if (node.action?.type === 'set-state') keys.push({ key: node.action.key, toggle: false });
    if (node.action?.type === 'toggle-state') keys.push({ key: node.action.key, toggle: true });
    for (const { key, toggle } of keys) {
      if (state === null) {
        out.push({ severity: 'error', file, nodeId: node.id, message: `The app shell has no page state, so “${key}” means nothing here.` });
      } else if (!(key in state)) {
        out.push({ severity: 'error', file, nodeId: node.id, message: `This page declares no state “${key}”.` });
      } else if (toggle && typeof state[key] !== 'boolean') {
        out.push({ severity: 'error', file, nodeId: node.id, message: `“${key}” is not true/false, so it cannot be toggled.` });
      }
    }
  }
}

function checkTree(
  root: Node,
  file: string,
  where: 'page' | 'shell' | 'component',
  registry: Registry,
  out: SiteProblem[],
  rootScope: Scope = { kind: 'none' },
  content?: ContentSchema,
): void {
  const problem = (severity: SiteProblem['severity'], message: string, nodeId?: string) =>
    out.push({ severity, file, nodeId, message });

  if (where !== 'component' && root.type !== PAGE_ROOT) problem('error', `The root must be a ${PAGE_ROOT}, not “${root.type}”.`, root.id);

  let outlets = 0;
  const visit = (node: Node, scope: Scope): void => {
    if (node.type === OUTLET) outlets++;
    const definition = definitionFor(registry, node);
    if (!definition) {
      problem(
        'error',
        registry.has(node.type) ? `“${node.type}” has no version ${node.version}.` : `“${node.type}” is not a known component.`,
        node.id,
      );
      return;
    }

    const props = new Map(definition.props.map((p) => [p.name, p]));
    for (const [name, value] of Object.entries(node.props ?? {})) {
      const prop = props.get(name);
      if (!prop) problem('warning', `${definition.label} has no setting “${name}”; it is ignored.`, node.id);
      else if (!propValueSchema(prop).safeParse(value).success) {
        problem('error', `${definition.label} › ${prop.label}: ${JSON.stringify(value)} is not an allowed value.`, node.id);
      }
    }
    for (const device of ['tablet', 'mobile'] as const) {
      for (const [name, value] of Object.entries(node.responsive?.[device] ?? {})) {
        const prop = props.get(name);
        if (!prop?.responsive) {
          problem('warning', `${definition.label} › “${name}” cannot differ on ${device}; the override is ignored.`, node.id);
        } else if (!propValueSchema(prop).safeParse(value).success) {
          problem('error', `${definition.label} › ${prop.label} on ${device}: ${JSON.stringify(value)} is not an allowed value.`, node.id);
        }
      }
    }
    for (const prop of definition.props) {
      const bound = node.bind?.[prop.name] !== undefined;
      if (prop.required && !bound && (node.props?.[prop.name] === undefined || node.props[prop.name] === '')) {
        problem('error', `${definition.label} › ${prop.label} is required.`, node.id);
      }
    }

    // Bindings read the item a collection or a detail page provides; with none, they read nothing.
    for (const [name, path] of Object.entries(node.bind ?? {})) {
      const prop = props.get(name);
      if (!prop) {
        problem('warning', `${definition.label} has no setting “${name}” to bind.`, node.id);
      } else if (!BINDABLE[prop.kind]) {
        problem('error', `${definition.label} › ${prop.label} cannot be bound to content.`, node.id);
      } else if (scope.kind === 'none') {
        problem('error', `${definition.label} › ${prop.label} shows “${path}”, but nothing around it provides an item — put it inside a collection, or on a detail page.`, node.id);
      } else if (scope.kind === 'source' && content && !META.has(path) && !content.get(scope.key)?.has(path) && !content.get(scope.key)?.has('*')) {
        problem('error', `${definition.label} › ${prop.label} shows the field “${path}”, which ${scope.key} does not have.`, node.id);
      }
    }
    if (node.action?.type === 'navigate' && /:(slug|id)\b/.test(node.action.to) && scope.kind === 'none') {
      problem('warning', `${definition.label} links to ${node.action.to}, but there is no item here to fill “:slug” in.`, node.id);
    }

    if (node.action && !definition.actions?.includes(node.action.type)) {
      problem('warning', `${definition.label} does not run “${node.action.type}” actions; it is ignored.`, node.id);
    }

    if (node.type === 'dcms.collection' && content) {
      const source = sourceSchema.safeParse(node.props?.source);
      if (source.success && !content.has(sourceKey(source.data))) {
        problem('error', `The collection shows ${sourceKey(source.data)}, which this site’s plugins do not provide.`, node.id);
      }
    }

    const declared = new Set((definition.slots ?? []).map((s) => s.name));
    for (const [slot, children] of Object.entries(node.slots ?? {})) {
      if (!declared.has(slot)) {
        if (children.length) problem('warning', `${definition.label} has no slot “${slot}”; what is in it is kept but never shown.`, node.id);
        continue;
      }
      let childScope = scope;
      if (node.type === 'dcms.collection' && slot === 'item') {
        const source = sourceSchema.safeParse(node.props?.source);
        childScope = source.success ? { kind: 'source', key: sourceKey(source.data) } : { kind: 'unknown' };
      }
      children.forEach((child, index) => {
        const placement = canPlace(registry, node.type, slot, child.type, index);
        if (!placement.ok && registry.has(child.type)) problem('error', placement.reason, child.id);
        visit(child, childScope);
      });
    }

    if (node.type === 'dcms.image' && node.props?.src && !node.props.alt && !node.bind?.alt) {
      problem('warning', 'An image has no description (alt text) for people who cannot see it.', node.id);
    }
  };
  visit(root, rootScope);

  if (where === 'shell' && outlets !== 1) {
    problem('error', outlets === 0 ? 'The app shell has no place for the page content.' : 'The app shell shows the page content more than once.');
  }
  if (where !== 'shell' && outlets > 0) problem('error', 'Page content can only be placed in the app shell.');
}

export function sourceKey(source: Source): string {
  return isConnectionSource(source) ? `connections/${source.connection}${source.operation}` : `${source.instance}/${source.contentType}`;
}

/**
 * Developer components: a contract that parses, a source file beside it with a default export,
 * and no source file without a contract (it would never be placed). The source itself is checked
 * by the type checker and the build, not here.
 */
function checkCode(files: Readonly<Record<string, string>>, out: SiteProblem[]): CodeContract[] {
  const json = new Map<string, unknown>();
  for (const [path, text] of Object.entries(files)) {
    if (!codeContractOf(path)) continue;
    const parsed = parse(text);
    if (parsed.ok) json.set(path, parsed.value);
    else out.push({ severity: 'error', file: path, message: parsed.message });
  }
  const { contracts, problems } = readCodeContracts(json);
  for (const p of problems) out.push({ severity: 'error', file: p.path, message: p.message });
  for (const contract of contracts) {
    const source = files[codeSourcePath(contract.name)];
    if (source === undefined) {
      out.push({ severity: 'error', file: codeContractPath(contract.name), message: `has no source: create ${codeSourcePath(contract.name)}.` });
    } else if (!/export\s+default\b/.test(source)) {
      out.push({ severity: 'error', file: codeSourcePath(contract.name), message: 'needs a default export: the component the builder places.' });
    }
  }
  for (const path of Object.keys(files)) {
    const name = path.startsWith('src/components/') ? codeSourceOf(path) : null;
    if (name && !contracts.some((c) => c.name === name) && files[codeContractPath(name)] === undefined) {
      out.push({ severity: 'warning', file: path, message: `has no contract, so the builder cannot place it: add ${codeContractPath(name)}.` });
    }
  }
  return contracts;
}

export function checkVisualSite(files: Readonly<Record<string, string>>, given?: Registry, content?: ContentSchema): SiteProblem[] {
  const out: SiteProblem[] = [];

  // The site's own components first: every other document may use them.
  const componentJson = new Map<string, unknown>();
  for (const [path, text] of Object.entries(files)) {
    if (!componentFileOf(path)) continue;
    const json = parse(text);
    if (json.ok) componentJson.set(path, json.value);
    else out.push({ severity: 'error', file: path, message: json.message });
  }
  const components = readComponentDocs(componentJson);
  for (const p of components.problems) out.push({ severity: 'error', file: p.path, message: p.message });
  const code = checkCode(files, out);
  const built = siteRegistry([...BUILTIN_COMPONENTS, ...codeDefinitions(code)], components.docs);
  for (const message of built.problems) out.push({ severity: 'error', file: 'dcms/components', message });
  const registry = given ?? built.registry;

  for (const doc of components.docs) {
    // A template's item comes from wherever an instance is placed, which is not known here.
    checkTree(doc.root, `dcms/components/${doc.name}/v${doc.version}.json`, 'component', registry, out, { kind: 'unknown' }, content);
  }
  // A component that contains itself, directly or through others, would render forever.
  const deps = new Map<string, Set<string>>();
  for (const doc of components.docs) {
    const type = tenantType(doc.name);
    deps.set(type, new Set([...(deps.get(type) ?? []), ...componentDependencies(doc)]));
  }
  const reported = new Set<string>();
  const visit = (type: string, path: string[]): void => {
    if (path.includes(type)) {
      const cycle = [...path.slice(path.indexOf(type)), type];
      const key = [...cycle].sort().join();
      if (!reported.has(key)) {
        reported.add(key);
        out.push({ severity: 'error', file: 'dcms/components', message: `Components contain each other in a loop: ${cycle.join(' → ')}.` });
      }
      return;
    }
    for (const next of deps.get(type) ?? []) visit(next, [...path, type]);
  };
  for (const type of deps.keys()) visit(type, []);

  const theme = parse(files[THEME_JSON]);
  if (theme.ok && !themeTokensSchema.safeParse(theme.value).success) {
    out.push({ severity: 'error', file: THEME_JSON, message: 'is not a valid theme.' });
  } else if (!theme.ok && files[THEME_JSON] !== undefined) {
    out.push({ severity: 'error', file: THEME_JSON, message: theme.message });
  }

  const pageIds = new Set<string>();
  const pageData = new Map<string, Page['data']>();
  for (const [path, text] of Object.entries(files)) {
    const id = pageIdFromPath(path);
    if (!id) continue;
    const json = parse(text);
    if (!json.ok) {
      out.push({ severity: 'error', file: path, message: json.message });
      continue;
    }
    const page = pageSchema.safeParse(json.value);
    if (!page.success) {
      const issue = page.error.issues[0];
      out.push({ severity: 'error', file: path, message: `${issue?.path.join('.') || 'page'}: ${issue?.message}` });
      continue;
    }
    if (page.data.id !== id) out.push({ severity: 'error', file: path, message: `its id is “${page.data.id}” but the file is named “${id}”.` });
    pageIds.add(id);
    pageData.set(id, page.data.data);
    const scope: Scope = page.data.data ? { kind: 'source', key: sourceKey(page.data.data.source) } : { kind: 'none' };
    checkTree(page.data.root, path, 'page', registry, out, scope, content);
    checkState(page.data.root, path, page.data.state ?? {}, out);
    if (page.data.data && content && !content.has(sourceKey(page.data.data.source))) {
      out.push({ severity: 'error', file: path, message: `It shows one item of ${sourceKey(page.data.data.source)}, which this site’s plugins do not provide.` });
    }
  }

  const appJson = parse(files[APP_JSON]);
  if (!appJson.ok) {
    out.push({ severity: 'error', file: APP_JSON, message: appJson.message });
    return out;
  }
  const parsed = appSchema.safeParse(appJson.value);
  if (!parsed.success) {
    for (const issue of parsed.error.issues.slice(0, 5)) {
      out.push({ severity: 'error', file: APP_JSON, message: `${issue.path.join('.') || 'app'}: ${issue.message}` });
    }
    return out;
  }
  const app: App = parsed.data;

  const routed = new Set<string>();
  for (const route of app.routes) {
    routed.add(route.page);
    if (!pageIds.has(route.page)) {
      out.push({ severity: 'error', file: APP_JSON, message: `The route ${route.path} shows the page “${route.page}”, which does not exist.` });
    }
    const data = pageData.get(route.page);
    if (data && !route.path.split('/').includes(`:${data.param}`)) {
      out.push({ severity: 'error', file: APP_JSON, message: `The route ${route.path} shows a detail page, so it needs “:${data.param}” in it.` });
    }
  }
  if (!app.routes.some((r) => r.path === '/')) out.push({ severity: 'error', file: APP_JSON, message: 'No route serves “/”. The site has no home page.' });
  for (const id of pageIds) {
    if (!routed.has(id)) out.push({ severity: 'warning', file: `dcms/pages/${id}.json`, message: 'No route shows this page, so it is never published.' });
  }

  for (const [menu, items] of Object.entries(app.navigation ?? {})) {
    for (const to of navPaths(items)) {
      if (isInternalPath(to) && !app.routes.some((r) => routeServes(r.path, to))) {
        out.push({ severity: 'warning', file: APP_JSON, message: `The “${menu}” menu links to ${to}, which no route serves.` });
      }
    }
  }

  if (app.shell) {
    checkTree(app.shell, APP_JSON, 'shell', registry, out, { kind: 'none' }, content);
    checkState(app.shell, APP_JSON, null, out);
  }
  return out;
}

export function formatProblems(problems: readonly SiteProblem[]): string {
  if (problems.length === 0) return 'No problems: every page renders, every route has a page, every component is where it may go.';
  return problems
    .map((p) => `${p.severity}: ${p.file}${p.nodeId ? ` (node ${p.nodeId})` : ''} — ${p.message}`)
    .join('\n');
}
