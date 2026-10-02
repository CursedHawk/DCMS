import {
  DataClientContext,
  NodeBoundary,
  PreviewItemsContext,
  RegistryContext,
  RenderModeContext,
  bindAction,
  bindProps,
  definitionFor,
  SLOT_SHELL_STYLE,
  SiteContext,
  type ComponentDefinition,
  type SlotLayout,
} from '@dcms/site-runtime';
import type { Component } from 'grapesjs';
import { useLayoutEffect, useRef } from 'react';
import { scopeOf } from '../data';
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

export function CanvasNode({ model, definition: latest }: { model: Component; definition: ComponentDefinition }) {
  const extra = (model.get(EXTRA) ?? {}) as NodeExtra;
  // Read here rather than passed in: each node is its own React root, so a store is the one
  // thing every root sees change at once (a menu edited in the Pages panel, say).
  const app = useVisual((s) => s.app);
  const registry = useVisual((s) => s.registry);
  // An instance pinned to an older version of a component draws that version.
  const definition = definitionFor(registry, { type: latest.type, version: extra.version }) ?? latest;
  const Impl = definition.component;
  const dataClient = useVisual((s) => s.dataClient);
  // The item this node's bindings read: its collection's first, or the detail page's.
  const scope = scopeOf(model, useVisual((s) => s.pageItem !== null));
  const item = useVisual((s) =>
    scope?.kind === 'collection' ? (s.previewItems[scope.nodeId]?.[0] ?? null) : scope?.kind === 'page' ? s.pageItem : null,
  );
  const count = useVisual((s) => (scope?.kind === 'collection' ? (s.previewItems[scope.nodeId]?.length ?? 0) : 1));
  const itemScope = item ? { item, index: 0, count } : null;
  const props = bindProps((model.get(PROPS) ?? {}) as Record<string, unknown>, extra.bind, definition.props, itemScope);
  return (
    <RenderModeContext.Provider value="edit">
      <SiteContext.Provider value={{ app }}>
        <RegistryContext.Provider value={registry}>
        <DataClientContext.Provider value={dataClient}>
        <PreviewItemsContext.Provider value={PREVIEW_SINK}>
        <NodeBoundary type={definition.type}>
          <Impl
            nodeId={model.get(ID) as string}
            props={props}
            action={bindAction(extra.action, itemScope)}
            responsive={extra.responsive}
            slot={(name, layout) => <CanvasSlot model={model} name={name} layout={layout} />}
          />
        </NodeBoundary>
        </PreviewItemsContext.Provider>
        </DataClientContext.Provider>
        </RegistryContext.Provider>
      </SiteContext.Provider>
    </RenderModeContext.Provider>
  );
}

/** Collections on the canvas report what they fetched here, for the nodes in their item template. */
const PREVIEW_SINK = { publish: (nodeId: string, items: readonly unknown[]) => useVisual.getState().publishItems(nodeId, items as never) };
