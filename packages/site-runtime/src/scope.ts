import { createContext, type ReactNode } from 'react';
import type { Node } from './document';
import type { ComponentDefinition, Registry, SlotLayout } from './registry';

/**
 * Render-time context shared by the renderer and tenant components (tenant.tsx), kept apart so
 * neither module imports the other.
 */

/** The registry a render is happening under, for components that render other components. */
export const RegistryContext = createContext<Registry | null>(null);

/**
 * The tenant component whose template is being drawn, if any: which inner slots are really the
 * instance's own slots. A tenant component restores the outer scope for the instance's children,
 * so a page node that happens to share an id with a template node is never mistaken for it.
 */
export interface TemplateScope {
  slotFor(nodeId: string, slot: string, layout?: SlotLayout): ReactNode | undefined;
}
export const TemplateScopeContext = createContext<TemplateScope | null>(null);

/** The definition a node renders with: its pinned version when it has one. */
export function definitionFor(registry: Registry, node: Pick<Node, 'type' | 'version'>): ComponentDefinition | undefined {
  const latest = registry.get(node.type);
  if (!latest || node.version === undefined || node.version === latest.version) return latest;
  return latest.olderVersions?.[node.version];
}
