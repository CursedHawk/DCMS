import {
  type ComponentNode,
  type History,
  type SiteDefinition,
  canRedo,
  canUndo,
  initHistory,
  insertNode,
  moveNode,
  newId,
  push,
  redo,
  removeNode,
  undo,
  updatePageRoot,
  updateProps,
} from '@dcms/editor-core';
import { findRegistration } from '@dcms/site-components';
import { create } from 'zustand';

interface EditorState {
  history: History<SiteDefinition>;
  pageId: string | null;
  selectedId: string | null;
  dirty: boolean;

  load: (def: SiteDefinition) => void;
  selectPage: (pageId: string) => void;
  select: (id: string | null) => void;
  addComponent: (type: string, parentId: string) => void;
  removeSelected: () => void;
  updateSelectedProps: (props: Record<string, unknown>) => void;
  move: (nodeId: string, parentId: string, index: number) => void;
  setBinding: (nodeId: string, propPath: string, instanceSlug: string) => void;
  undo: () => void;
  redo: () => void;
  markSaved: () => void;

  definition: () => SiteDefinition;
  currentRoot: () => ComponentNode | null;
  canUndo: () => boolean;
  canRedo: () => boolean;
}

function pageRoot(def: SiteDefinition, pageId: string | null): ComponentNode | null {
  return def.pages.find((p) => p.id === pageId)?.root ?? null;
}

function commit(state: EditorState, nextRoot: ComponentNode): Partial<EditorState> {
  const def = state.history.present;
  if (!state.pageId) return {};
  return { history: push(state.history, updatePageRoot(def, state.pageId, nextRoot)), dirty: true };
}

export const useEditor = create<EditorState>((set, get) => ({
  history: initHistory({ version: 1, theme: { colors: {}, fonts: {} }, pages: [], nav: [] }),
  pageId: null,
  selectedId: null,
  dirty: false,

  load: (def) => set({ history: initHistory(def), pageId: def.pages[0]?.id ?? null, selectedId: null, dirty: false }),
  selectPage: (pageId) => set({ pageId, selectedId: null }),
  select: (id) => set({ selectedId: id }),

  addComponent: (type, parentId) => {
    const state = get();
    const root = pageRoot(state.history.present, state.pageId);
    if (!root) return;
    const reg = findRegistration(type);
    const node: ComponentNode = {
      id: newId(type),
      type,
      props: { ...(reg?.defaultProps ?? {}) },
      children: reg?.acceptsChildren ? [] : undefined,
    };
    set({ ...commit(state, insertNode(root, parentId, node)), selectedId: node.id });
  },

  removeSelected: () => {
    const state = get();
    const root = pageRoot(state.history.present, state.pageId);
    if (!root || !state.selectedId || state.selectedId === root.id) return;
    set({ ...commit(state, removeNode(root, state.selectedId)), selectedId: null });
  },

  updateSelectedProps: (props) => {
    const state = get();
    const root = pageRoot(state.history.present, state.pageId);
    if (!root || !state.selectedId) return;
    set(commit(state, updateProps(root, state.selectedId, props)));
  },

  move: (nodeId, parentId, index) => {
    const state = get();
    const root = pageRoot(state.history.present, state.pageId);
    if (!root) return;
    set(commit(state, moveNode(root, nodeId, parentId, index)));
  },

  setBinding: (nodeId, propPath, instanceSlug) => {
    const state = get();
    const root = pageRoot(state.history.present, state.pageId);
    if (!root) return;
    const apply = (n: ComponentNode): ComponentNode => {
      if (n.id === nodeId) {
        return { ...n, bindings: [{ propPath, source: { instanceSlug, query: {} } }] };
      }
      return n.children ? { ...n, children: n.children.map(apply) } : n;
    };
    set(commit(state, apply(root)));
  },

  undo: () => set((s) => ({ history: undo(s.history), dirty: true })),
  redo: () => set((s) => ({ history: redo(s.history), dirty: true })),
  markSaved: () => set({ dirty: false }),

  definition: () => get().history.present,
  currentRoot: () => pageRoot(get().history.present, get().pageId),
  canUndo: () => canUndo(get().history),
  canRedo: () => canRedo(get().history),
}));
