import {
  NodeBoundary,
  RenderModeContext,
  SLOT_SHELL_STYLE,
  SiteContext,
  type ComponentDefinition,
  type SlotLayout,
} from '@dcms/site-runtime';
import type { Component } from 'grapesjs';
import { useLayoutEffect, useRef } from 'react';
import { useVisual } from '../store';
import { applyLayout, slotOf, type SlotViewState } from './slots';
import { EXTRA, ID, PROPS, type NodeExtra } from './tree';

/** What a node's React root renders on the canvas, and where its slots' GrapesJS views go. See types.tsx. */

/** Where GrapesJS draws one slot: its view element is moved into this shell after every commit. */
function CanvasSlot({ model, name, layout }: { model: Component; name: string; layout?: SlotLayout }) {
  const shell = useRef<HTMLSpanElement>(null);
  useLayoutEffect(() => {
    const view = slotOf(model, name)?.getView() as unknown as SlotViewState | undefined;
    if (!shell.current || !view) return;
    view.layout = layout;
    applyLayout(view);
    if (view.el.parentNode !== shell.current) shell.current.appendChild(view.el);
  });
  return <span ref={shell} style={SLOT_SHELL_STYLE} />;
}

export function CanvasNode({ model, definition }: { model: Component; definition: ComponentDefinition }) {
  const Impl = definition.component;
  const extra = (model.get(EXTRA) ?? {}) as NodeExtra;
  // Read here rather than passed in: each node is its own React root, so a store is the one
  // thing every root sees change at once (a menu edited in the Pages panel, say).
  const app = useVisual((s) => s.app);
  return (
    <RenderModeContext.Provider value="edit">
      <SiteContext.Provider value={{ app }}>
        <NodeBoundary type={definition.type}>
          <Impl
            nodeId={model.get(ID) as string}
            props={(model.get(PROPS) ?? {}) as Record<string, unknown>}
            action={extra.action}
            responsive={extra.responsive}
            slot={(name, layout) => <CanvasSlot model={model} name={name} layout={layout} />}
          />
        </NodeBoundary>
      </SiteContext.Provider>
    </RenderModeContext.Provider>
  );
}
