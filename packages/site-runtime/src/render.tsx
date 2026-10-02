import { Component, type CSSProperties, type ContextType, type ErrorInfo, type ReactNode } from 'react';
import type { Node } from './document';
import type { Registry, SlotLayout } from './registry';
import { RenderModeContext, useRenderMode } from './renderMode';

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
export function RenderNode({ node, registry }: RenderNodeProps) {
  return (
    <div className={NODE_CLASS} data-dcms-node={node.id} data-dcms-type={node.type}>
      <NodeBoundary type={node.type}>
        <NodeBody node={node} registry={registry} />
      </NodeBoundary>
    </div>
  );
}

function NodeBody({ node, registry }: RenderNodeProps) {
  const mode = useRenderMode();
  const definition = registry.get(node.type);
  if (!definition) return mode === 'edit' ? <Problem>Unknown component “{node.type}”</Problem> : null;

  const Impl = definition.component;
  const slot = (name: string, layout?: SlotLayout) => (
    <span style={SLOT_SHELL_STYLE}>
      <div className={slotClassName(layout)} style={layout?.style} data-dcms-slot={name}>
        {(node.slots?.[name] ?? []).map((child) => (
          <RenderNode key={child.id} node={child} registry={registry} />
        ))}
      </div>
    </span>
  );
  return <Impl nodeId={node.id} props={node.props ?? {}} action={node.action} slot={slot} />;
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
