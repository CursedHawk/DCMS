import { Component, useContext, type CSSProperties, type ContextType, type ErrorInfo, type ReactNode } from 'react';
import { bindAction, bindProps, useItem } from './data';
import { useSite } from './site';
import { useShown } from './state';
import { nodeCssSchema, type Node } from './document';
import type { Registry, SlotLayout } from './registry';
import { RenderModeContext, useRenderMode } from './renderMode';
import { RegistryContext, TemplateScopeContext, definitionFor } from './scope';

/**
 * Turning a node tree into React.
 *
 * The DOM is the same shape here and on the builder canvas, by construction:
 *
 * ```
 * <div class="dcms-node" data-dcms-node="n1" data-dcms-type="dcms.stack">   one per node
 *   …the component's own output…
 *     <span style="display:contents">
 *       <div class="dcms-slot …" data-dcms-slot="default">                 one per slot
 *         <div class="dcms-node" …>…</div>                                 its children
 * ```
 *
 * On the canvas the `.dcms-node` and `.dcms-slot` elements are GrapesJS's own views — it needs
 * a real box per component to select, highlight and drop into — so the published site carries
 * the same wrappers rather than a leaner DOM that would lay out differently from what the
 * author arranged. Stylesheets must therefore never use a child combinator across a node or a
 * slot boundary.
 */

export const NODE_CLASS = 'dcms-node';
export const SLOT_CLASS = 'dcms-slot';

/** The `display: contents` shell a slot sits in, so it adds no box of its own. */
export const SLOT_SHELL_STYLE: CSSProperties = { display: 'contents' };

export function slotClassName(layout?: SlotLayout): string {
  return layout?.className ? `${SLOT_CLASS} ${layout.className}` : SLOT_CLASS;
}

interface RenderNodeProps {
  node: Node;
  registry: Registry;
}

/** One node and everything under it, as the published site renders it. */
/**
 * An instance's own CSS, scoped to its component's element. The schema has already refused
 * anything but declarations; this re-checks, since a tree may reach a renderer unvalidated.
 */
export function NodeCss({ id, css }: { id: string; css?: string }) {
  if (!css || !nodeCssSchema.safeParse(css).success) return null;
  return <style>{`[data-dcms-node="${id}"] > :not(style) { ${css} }`}</style>;
}

export function RenderNode({ node, registry }: RenderNodeProps) {
  const shown = useShown(node.when);
  const mode = useRenderMode();
  // Hidden by page state: gone from the site, wrapper and all, so it takes no gap in a stack.
  if (!shown && mode === 'live') return null;
  return (
    <div className={NODE_CLASS} data-dcms-node={node.id} data-dcms-type={node.type}>
      <NodeCss id={node.id} css={node.css} />
      <RegistryContext.Provider value={registry}>
        <NodeBoundary type={node.type}>
          <NodeBody node={node} registry={registry} />
        </NodeBoundary>
      </RegistryContext.Provider>
    </div>
  );
}

function NodeBody({ node, registry }: RenderNodeProps) {
  const mode = useRenderMode();
  const scope = useContext(TemplateScopeContext);
  const item = useItem();
  const locale = useSite().app?.locale;
  const definition = definitionFor(registry, node);
  if (!definition) {
    const known = registry.has(node.type);
    return mode === 'edit' ? (
      <Problem>{known ? `“${node.type}” has no version ${node.version}` : `Unknown component “${node.type}”`}</Problem>
    ) : null;
  }

  const Impl = definition.component;
  // Inside a tenant component's template, a slot wired to one of the component's own slots is
  // drawn by the instance (see tenant.tsx); everything else renders its own children.
  const slot = (name: string, layout?: SlotLayout) => scope?.slotFor(node.id, name, layout) ?? (
    <span style={SLOT_SHELL_STYLE}>
      <div className={slotClassName(layout)} style={layout?.style} data-dcms-slot={name}>
        {(node.slots?.[name] ?? []).map((child) => (
          <RenderNode key={child.id} node={child} registry={registry} />
        ))}
      </div>
    </span>
  );
  // Inside a collection or on a detail page, bound props come from the item in scope.
  const props = bindProps(node.props ?? {}, node.bind, definition.props, item, locale);
  return <Impl nodeId={node.id} props={props} action={bindAction(node.action, item)} responsive={node.responsive} slot={slot} />;
}

function Problem({ children }: { children: ReactNode }) {
  return (
    <div className="dcms-problem" role="alert">
      {children}
    </div>
  );
}

/**
 * One broken component must not take the page down with it. On the site the failed node simply
 * renders nothing; in the editor it says so, where the author can see and fix it.
 */
export class NodeBoundary extends Component<{ type: string; children: ReactNode }, { failed: boolean }> {
  static contextType = RenderModeContext;
  declare context: ContextType<typeof RenderModeContext>;
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(`dcms: “${this.props.type}” failed to render`, error, info.componentStack);
  }

  render() {
    if (!this.state.failed) return this.props.children;
    return this.context === 'edit' ? <Problem>“{this.props.type}” could not be drawn</Problem> : null;
  }
}
