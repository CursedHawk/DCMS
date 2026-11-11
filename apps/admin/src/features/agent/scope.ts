import { can, type MyPermissions } from '@dcms/core';
import { ASSISTANT_TOOLS } from '../assistant/tools';
import type { ToolSpec } from './contracts';

/**
 * How far outside the site's own files a run may reach.
 *
 * <p>Deliberately a <b>separate axis from mode</b>. Mode answers "how much does it do without
 * asking"; scope answers "what is it allowed to touch at all". Collapsing them produces the
 * familiar mess where turning up autonomy silently widens reach — an operator who wanted the
 * agent to stop asking about edits does not thereby want it publishing articles.</p>
 */
export type AgentScope = 'site' | 'site+tenant';

export const AGENT_SCOPES: readonly AgentScope[] = ['site', 'site+tenant'];

/** The default: the agent edits code and nothing else. */
export const DEFAULT_SCOPE: AgentScope = 'site';

/**
 * Tool names that reach the tenant's real content and configuration rather than the site's
 * files: every console assistant tool (the IDE agent offers them as TENANT_TOOLS), and the
 * preview's requests. Derived, so a tool added to the console is never offered in the narrow
 * site scope by default.
 */
const TENANT_NAMES = new Set([...ASSISTANT_TOOLS.map((tool) => tool.name), 'preview_request']);

export function isTenantTool(name: string): boolean {
  return TENANT_NAMES.has(name);
}

/**
 * Filter a registry down to what this run may use.
 *
 * <p>Two gates, and both matter for different reasons.</p>
 *
 * <p><b>Scope</b> is the operator's choice about reach. <b>Permission</b> is the caller's actual
 * authority — and a tool the caller cannot use is removed rather than left to fail, because a
 * model told a tool exists will reach for it, and an assistant that keeps announcing it cannot
 * do what it just offered reads as broken rather than careful.</p>
 *
 * <p>Neither is security. The browser decides what to <i>offer</i>; the server decides what to
 * <i>allow</i>, independently, on every call.</p>
 */
export function toolsFor<T>(
  tools: readonly ToolSpec<T>[],
  scope: AgentScope,
  permissions: MyPermissions | undefined,
): ToolSpec<T>[] {
  return tools.filter((tool) => {
    if (isTenantTool(tool.name) && scope === 'site') return false;
    if (tool.permission && !can(permissions, tool.permission)) return false;
    return true;
  });
}

const SCOPE_KEY = 'dcms.agent.scope';

/**
 * The scope this browser starts in.
 *
 * <p>Always the narrowest. Unlike mode, scope is not remembered at all: "the agent may edit your
 * live content" is a decision that should be made by the person sitting there for the task in
 * front of them, not restored from a fortnight ago because a browser still had the key.</p>
 */
export function storedScope(): AgentScope {
  return DEFAULT_SCOPE;
}

export function rememberScope(scope: AgentScope): void {
  // Intentionally records only the *narrow* choice, so nothing can widen silently on reload.
  try {
    if (scope === 'site') localStorage.setItem(SCOPE_KEY, scope);
    else localStorage.removeItem(SCOPE_KEY);
  } catch {
    // A browser refusing storage is not a reason to refuse the agent.
  }
}
