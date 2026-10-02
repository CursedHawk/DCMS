import { builtinRegistry } from './components';
import { appSchema, pageSchema, walk, type App, type NavItem, type Node } from './document';
import { isInternalPath } from './ids';
import { APP_JSON, THEME_JSON, pageIdFromPath } from './paths';
import { propValueSchema } from './props';
import { canPlace, type Registry } from './registry';
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

function checkTree(root: Node, file: string, where: 'page' | 'shell', registry: Registry, out: SiteProblem[]): void {
  const problem = (severity: SiteProblem['severity'], message: string, nodeId?: string) =>
    out.push({ severity, file, nodeId, message });

  if (root.type !== PAGE_ROOT) problem('error', `The root must be a ${PAGE_ROOT}, not “${root.type}”.`, root.id);

  let outlets = 0;
  for (const node of walk(root)) {
    if (node.type === OUTLET) outlets++;
    const definition = registry.get(node.type);
    if (!definition) {
      problem('error', `“${node.type}” is not a known component.`, node.id);
      continue;
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
      if (prop.required && (node.props?.[prop.name] === undefined || node.props[prop.name] === '')) {
        problem('error', `${definition.label} › ${prop.label} is required.`, node.id);
      }
    }

    if (node.action && !definition.actions?.includes(node.action.type)) {
      problem('warning', `${definition.label} does not run “${node.action.type}” actions; it is ignored.`, node.id);
    }

    const declared = new Set((definition.slots ?? []).map((s) => s.name));
    for (const [slot, children] of Object.entries(node.slots ?? {})) {
      if (!declared.has(slot)) {
        if (children.length) problem('warning', `${definition.label} has no slot “${slot}”; what is in it is kept but never shown.`, node.id);
        continue;
      }
      children.forEach((child, index) => {
        const placement = canPlace(registry, node.type, slot, child.type, index);
        if (!placement.ok && registry.has(child.type)) problem('error', placement.reason, child.id);
      });
    }

    if (node.type === 'dcms.image' && node.props?.src && !node.props.alt) {
      problem('warning', 'An image has no description (alt text) for people who cannot see it.', node.id);
    }
  }

  if (where === 'shell' && outlets !== 1) {
    problem('error', outlets === 0 ? 'The app shell has no place for the page content.' : 'The app shell shows the page content more than once.');
  }
  if (where === 'page' && outlets > 0) problem('error', 'Page content can only be placed in the app shell.');
}

export function checkVisualSite(files: Readonly<Record<string, string>>, registry: Registry = builtinRegistry): SiteProblem[] {
  const out: SiteProblem[] = [];

  const theme = parse(files[THEME_JSON]);
  if (theme.ok && !themeTokensSchema.safeParse(theme.value).success) {
    out.push({ severity: 'error', file: THEME_JSON, message: 'is not a valid theme.' });
  } else if (!theme.ok && files[THEME_JSON] !== undefined) {
    out.push({ severity: 'error', file: THEME_JSON, message: theme.message });
  }

  const pageIds = new Set<string>();
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
    checkTree(page.data.root, path, 'page', registry, out);
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

  if (app.shell) checkTree(app.shell, APP_JSON, 'shell', registry, out);
  return out;
}

export function formatProblems(problems: readonly SiteProblem[]): string {
  if (problems.length === 0) return 'No problems: every page renders, every route has a page, every component is where it may go.';
  return problems
    .map((p) => `${p.severity}: ${p.file}${p.nodeId ? ` (node ${p.nodeId})` : ''} — ${p.message}`)
    .join('\n');
}
