import { NODE_CLASS, canPlace, defaultProps, type Registry } from '@dcms/site-runtime';
import type { Component, Editor, ToolbarButtonProps } from 'grapesjs';
import { createRoot, type Root } from 'react-dom/client';
import i18n from '../../../lib/i18n';
import { thumbnailOf } from '../catalog/look';
import { CanvasNode } from './CanvasNode';
import { useVisual } from '../store';
import { applyLayout, type SlotViewState } from './slots';
import { EXTRA, ID, PROPS, RAW, SLOT, SLOT_TYPE, UNKNOWN_TYPE, newNodeId, nodeFromStarter, toGrapes } from './tree';

/**
 * GrapesJS component types for the Mode D registry, with React drawing them.
 *
 * GrapesJS owns the tree, selection, drag and drop, layers and undo; React owns what each
 * component looks like. They meet at two kinds of element, which are real boxes on the canvas
 * and on the published site alike (see `render.tsx` in `@dcms/site-runtime`):
 *
 * - **A node's view element** is the `.dcms-node` wrapper. Its view mounts a React root on it
 *   and renders the same component the site runs, with `RenderMode` set to `edit`.
 * - **A slot's view element** is the `.dcms-slot` element. GrapesJS renders a node's slot views
 *   into a detached holder (`getChildrenContainer`), and the component's own React output moves
 *   each one into place. GrapesJS still owns everything inside a slot, so dropping into it,
 *   sorting it and selecting in it are GrapesJS's own, unmodified behaviour.
 *
 * Two GrapesJS habits have to be headed off, both silent if they are not:
 * `updateAttributes` clears every attribute on the element before re-applying its own, and
 * `updateContent` overwrites the children container's `innerHTML`. The overrides below re-apply
 * ours after the first, and point the second at the holder so it never touches React's DOM.
 */

/** What the views keep on themselves. GrapesJS's view types are loose, so this is spelled out once. */
interface NodeViewState {
  el: HTMLElement;
  model: Component;
  root?: Root;
  holder?: HTMLElement;
  renderReact(): void;
}

/** Command and editor event: open the Sections palette to add a section below the selection. */
export const ADD_SECTION = 'dcms:add-section';

export function registerVisualTypes(editor: Editor, registry: Registry): void {
  const components = editor.Components;
  const BaseView = components.getType('default')!.view;
  const base = BaseView.prototype as unknown as {
    updateAttributes(this: unknown): void;
    updateClasses(this: unknown): void;
  };

  // The selection toolbar's "add a section below": the Sections palette answers it, and the
  // section it inserts goes after the band holding the selection.
  editor.Commands.add(ADD_SECTION, { run: (ed) => ed.trigger(ADD_SECTION) });
  const addSection = {
    label: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>',
    command: ADD_SECTION,
    attributes: { title: i18n.t('visual.palette.addSection'), class: 'dcms-tb-add-section' },
  };

  components.addType(SLOT_TYPE, {
    model: {
      defaults: {
        tagName: 'div',
        draggable: false,
        removable: false,
        copyable: false,
        selectable: false,
        hoverable: false,
        // The one placement rule (canPlace) decides, for drag and drop exactly as for the AI.
        droppable: (source: Component, target: Component) => {
          const parent = target.parent();
          if (!parent) return false;
          if (useVisual.getState().blockedTypes.has(source.get('type') ?? '')) return false;
          const siblings = target.components().models.filter((c) => c !== source).length;
          return canPlace(registry, parent.get('type') ?? '', target.get(SLOT), source.get('type') ?? '', siblings).ok;
        },
      },
    },
    view: {
      updateAttributes(this: SlotViewState) {
        base.updateAttributes.call(this);
        applyLayout(this);
      },
      updateClasses(this: SlotViewState) {
        base.updateClasses.call(this);
        applyLayout(this);
      },
    },
  });

  components.addType(UNKNOWN_TYPE, {
    model: { defaults: { tagName: 'div', droppable: false, copyable: false, name: 'Unknown component' } },
    view: {
      onRender(this: NodeViewState) {
        const raw = this.model.get(RAW) as { type?: string } | undefined;
        this.el.className = `${NODE_CLASS} dcms-problem`;
        this.el.textContent = `Unknown component “${raw?.type ?? '?'}” — kept as it is`;
      },
    },
  });

  for (const definition of registry.values()) {
    const placeable = definition.draggable !== false;
    components.addType(definition.type, {
      model: {
        defaults: {
          tagName: 'div',
          name: definition.label,
          droppable: false,
          draggable: placeable,
          removable: placeable,
          copyable: placeable,
        },
        init(this: Component) {
          // A component dropped from the palette arrives with only its type. Everything a
          // node needs is filled in here, silently: it is part of being created, not an edit.
          // A copy (GrapesJS constructs every clone, nested ones too, with `forCloning`) gets
          // its own id at birth rather than sharing the original's until the next save.
          const cloning = (this as unknown as { opt?: { forCloning?: boolean } }).opt?.forCloning;
          if (!this.get(ID) || cloning) this.set(ID, newNodeId(), { silent: true });
          if (!this.get(PROPS)) this.set(PROPS, defaultProps(definition), { silent: true });
          // A site's own component is pinned to the version it was placed with, so changing the
          // component later never changes a page behind its author's back.
          if (!this.get(EXTRA)) this.set(EXTRA, definition.template ? { version: definition.version } : {}, { silent: true });
          const toolbar = this.get('toolbar') as ToolbarButtonProps[] | undefined;
          if (placeable && toolbar) this.set('toolbar', [...toolbar, addSection], { silent: true });
          if (this.components().length === 0 && definition.slots?.length) {
            // A fresh one starts with its starter children (an accordion with two questions).
            this.components(
              definition.slots.map((slot) => ({
                type: SLOT_TYPE,
                [SLOT]: slot.name,
                name: slot.label ?? slot.name,
                components: (definition.starter?.[slot.name] ?? []).map((s) => toGrapes(nodeFromStarter(s, registry), registry)),
              })),
            );
          }
        },
      },
      view: {
        init(this: NodeViewState & { listenTo: (o: Component, e: string, f: () => void) => void }) {
          this.listenTo(this.model, `change:${PROPS} change:${EXTRA}`, () => this.renderReact());
        },
        getChildrenContainer(this: NodeViewState) {
          this.holder ??= this.el.ownerDocument.createElement('div');
          return this.holder;
        },
        updateAttributes(this: NodeViewState) {
          base.updateAttributes.call(this);
          this.el.setAttribute('data-dcms-node', this.model.get(ID) as string);
          this.el.setAttribute('data-dcms-type', definition.type);
        },
        updateClasses(this: NodeViewState) {
          base.updateClasses.call(this);
          this.el.classList.add(NODE_CLASS);
        },
        renderReact(this: NodeViewState) {
          this.root ??= createRoot(this.el);
          this.root.render(<CanvasNode model={this.model} definition={definition} />);
        },
        onRender(this: NodeViewState) {
          this.renderReact();
        },
        removed(this: NodeViewState) {
          const root = this.root;
          this.root = undefined;
          // Not synchronously: GrapesJS can remove a view while React is mid-render (an undo
          // fired from a panel), and React refuses to unmount a root during a render.
          if (root) queueMicrotask(() => root.unmount());
        },
      },
    });
  }

  // Re-registration (a component was created, renamed or removed) must also take away the
  // blocks of components that no longer exist.
  for (const block of [...editor.Blocks.getAll().models]) {
    const id = String(block.getId());
    if (id.startsWith('dcms-d:') && !registry.has(id.slice('dcms-d:'.length))) editor.Blocks.remove(id);
  }
  for (const definition of registry.values()) {
    if (definition.draggable === false) continue;
    editor.Blocks.add(`dcms-d:${definition.type}`, {
      label: definition.label,
      category: definition.category,
      media: BLOCK_ICON,
      // The palette's wireframe and hover text (BlocksPanel reads `thumbnail` and `docs`).
      thumbnail: thumbnailOf(definition),
      docs: definition.description,
      content: { type: definition.type },
    } as Parameters<Editor['Blocks']['add']>[1]);
  }
}

const BLOCK_ICON =
  '<svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.5"><rect x="3.5" y="3.5" width="17" height="17" rx="2.5"/><path d="M8 9h8M8 12h8M8 15h5"/></svg>';
