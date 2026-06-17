import type { Breakpoint, ComponentNode, NodeLayout, SiteDefinition } from './schema';

/** Immutable component-tree operations. Each returns a new tree; inputs are never mutated. */

export function findNode(root: ComponentNode, id: string): ComponentNode | undefined {
  if (root.id === id) return root;
  for (const child of root.children ?? []) {
    const found = findNode(child, id);
    if (found) return found;
  }
  return undefined;
}

export function findParent(root: ComponentNode, childId: string): ComponentNode | undefined {
  for (const child of root.children ?? []) {
    if (child.id === childId) return root;
    const found = findParent(child, childId);
    if (found) return found;
  }
  return undefined;
}

/** Insert `node` as a child of `parentId` at `index` (default: append). */
export function insertNode(
  root: ComponentNode,
  parentId: string,
  node: ComponentNode,
  index?: number,
): ComponentNode {
  return mapNode(root, (n) => {
    if (n.id !== parentId) return n;
    const children = [...(n.children ?? [])];
    children.splice(index ?? children.length, 0, node);
    return { ...n, children };
  });
}

export function removeNode(root: ComponentNode, id: string): ComponentNode {
  return mapNode(root, (n) => {
    if (!n.children?.length) return n;
    return { ...n, children: n.children.filter((c) => c.id !== id) };
  });
}

export function updateProps(root: ComponentNode, id: string, props: Record<string, unknown>): ComponentNode {
  return mapNode(root, (n) => (n.id === id ? { ...n, props: { ...n.props, ...props } } : n));
}

type LayoutPatch = Partial<Pick<NodeLayout, 'x' | 'y' | 'w' | 'h' | 'z'>>;

/**
 * Merge an absolute-layout patch into a node. On the desktop breakpoint the patch
 * updates the base box; on tablet/mobile it accumulates a per-breakpoint override.
 */
export function updateLayout(
  root: ComponentNode,
  id: string,
  patch: LayoutPatch,
  breakpoint: Breakpoint = 'desktop',
): ComponentNode {
  return mapNode(root, (n) => {
    if (n.id !== id) return n;
    const base: NodeLayout = n.layout ?? { x: 0, y: 0, w: 200, h: 80 };
    if (breakpoint === 'desktop') {
      return { ...n, layout: { ...base, ...patch } };
    }
    const breakpoints = { ...(base.breakpoints ?? {}) };
    breakpoints[breakpoint] = { ...(breakpoints[breakpoint] ?? {}), ...patch };
    return { ...n, layout: { ...base, breakpoints } };
  });
}

export function replaceNode(root: ComponentNode, id: string, replacement: ComponentNode): ComponentNode {
  if (root.id === id) return replacement;
  return mapNode(root, (n) => (n.id === id ? replacement : n));
}

/** Move `nodeId` to be a child of `targetParentId` at `index`. No-op if it would create a cycle. */
export function moveNode(
  root: ComponentNode,
  nodeId: string,
  targetParentId: string,
  index?: number,
): ComponentNode {
  if (nodeId === targetParentId) return root;
  const node = findNode(root, nodeId);
  if (!node) return root;
  // Disallow moving a node into its own subtree.
  if (findNode(node, targetParentId)) return root;
  const without = removeNode(root, nodeId);
  return insertNode(without, targetParentId, node, index);
}

/** Apply `fn` to every node bottom-up, rebuilding children first. */
function mapNode(node: ComponentNode, fn: (n: ComponentNode) => ComponentNode): ComponentNode {
  const children = node.children?.map((c) => mapNode(c, fn));
  return fn(children ? { ...node, children } : node);
}

export function updatePageRoot(def: SiteDefinition, pageId: string, root: ComponentNode): SiteDefinition {
  return { ...def, pages: def.pages.map((p) => (p.id === pageId ? { ...p, root } : p)) };
}

let counter = 0;
export function newId(type: string): string {
  counter += 1;
  return `${type.toLowerCase()}-${Date.now().toString(36)}-${counter}`;
}
