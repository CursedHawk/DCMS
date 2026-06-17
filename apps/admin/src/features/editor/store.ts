import {
  type Breakpoint,
  type ComponentNode,
  type History,
  type NodeLayout,
  type Page,
  type SiteDefinition,
  canRedo,
  canUndo,
  effectiveLayout,
  initHistory,
  newId,
  push,
  redo,
  removeNode,
  undo,
  updateLayout as treeUpdateLayout,
  updatePageRoot,
  updateProps,
} from '@dcms/editor-core';
import { findRegistration } from '@dcms/site-components';
import { create } from 'zustand';
import { defaultSize } from './constants';

interface EditorState {
  history: History<SiteDefinition>;
  pageId: string | null;
  selectedId: string | null;
  breakpoint: Breakpoint;
  dirty: boolean;

  load: (def: SiteDefinition) => void;
  selectPage: (id: string) => void;
  select: (id: string | null) => void;
  setBreakpoint: (b: Breakpoint) => void;

  addNode: (type: string) => void;
  addGeneratedNode: (node: ComponentNode) => void;
  updateLayout: (id: string, patch: Partial<Pick<NodeLayout, 'x' | 'y' | 'w' | 'h' | 'z'>>) => void;
  updateSelectedProps: (props: Record<string, unknown>) => void;
  removeSelected: () => void;
  duplicateSelected: () => void;
  bringToFront: () => void;
  sendToBack: () => void;
  setBinding: (id: string, propPath: string, instanceSlug: string | null) => void;

  addPage: () => void;
  updatePageMeta: (patch: Partial<Pick<Page, 'title' | 'path'>> & { seoTitle?: string; seoDescription?: string }) => void;
  updateTheme: (patch: Partial<SiteDefinition['theme']>) => void;
  /** Replace the whole definition (used by AI full-site generation). */
  replaceDefinition: (def: SiteDefinition) => void;

  undo: () => void;
  redo: () => void;
  markSaved: () => void;

  definition: () => SiteDefinition;
  currentPage: () => Page | null;
  currentNodes: () => ComponentNode[];
  canUndo: () => boolean;
  canRedo: () => boolean;
}

const emptyDef: SiteDefinition = { version: 1, theme: { colors: {}, fonts: {} }, pages: [], nav: [] };

function makePage(): Page {
  return {
    id: newId('page'),
    path: '/',
    title: 'Home',
    seo: { title: 'Home' },
    canvas: { width: 1200, minHeight: 900 },
    root: { id: newId('root'), type: 'Section', props: {}, children: [] },
  };
}

/** Ensure every page has a canvas + root container (free-canvas requires both). */
function normalize(def: SiteDefinition): SiteDefinition {
  const pages = def.pages.length > 0 ? def.pages : [makePage()];
  return {
    ...def,
    pages: pages.map((p) => ({
      ...p,
      canvas: p.canvas ?? { width: 1200, minHeight: 900 },
      root: p.root ?? { id: newId('root'), type: 'Section', props: {}, children: [] },
    })),
  };
}

function pageOf(def: SiteDefinition, pageId: string | null): Page | null {
  return def.pages.find((p) => p.id === pageId) ?? null;
}

export const useEditor = create<EditorState>((set, get) => {
  // Commit a new root for the current page into history.
  function commitRoot(state: EditorState, nextRoot: ComponentNode, extra?: Partial<EditorState>) {
    if (!state.pageId) return;
    set({
      history: push(state.history, updatePageRoot(state.history.present, state.pageId, nextRoot)),
      dirty: true,
      ...extra,
    });
  }

  return {
    history: initHistory(emptyDef),
    pageId: null,
    selectedId: null,
    breakpoint: 'desktop',
    dirty: false,

    load: (def) => {
      const normalized = normalize(def);
      set({
        history: initHistory(normalized),
        pageId: normalized.pages[0]?.id ?? null,
        selectedId: null,
        dirty: false,
      });
    },

    selectPage: (id) => set({ pageId: id, selectedId: null }),
    select: (id) => set({ selectedId: id }),
    setBreakpoint: (b) => set({ breakpoint: b }),

    addNode: (type) => {
      const state = get();
      const page = pageOf(state.history.present, state.pageId);
      if (!page) return;
      const reg = findRegistration(type);
      const size = defaultSize(type);
      const count = page.root.children?.length ?? 0;
      const node: ComponentNode = {
        id: newId(type),
        type,
        props: { ...(reg?.defaultProps ?? {}) },
        children: reg?.acceptsChildren ? [] : undefined,
        layout: { x: 40 + (count % 6) * 24, y: 40 + (count % 6) * 24, w: size.w, h: size.h, z: count },
      };
      const nextRoot = { ...page.root, children: [...(page.root.children ?? []), node] };
      commitRoot(state, nextRoot, { selectedId: node.id });
    },

    addGeneratedNode: (incoming) => {
      const state = get();
      const page = pageOf(state.history.present, state.pageId);
      if (!page) return;
      const count = page.root.children?.length ?? 0;
      const node: ComponentNode = {
        ...incoming,
        id: incoming.id || newId(incoming.type || 'node'),
        layout: incoming.layout ?? { x: 40, y: 40 + count * 28, w: 600, h: 220, z: count },
      };
      const nextRoot = { ...page.root, children: [...(page.root.children ?? []), node] };
      commitRoot(state, nextRoot, { selectedId: node.id });
    },

    updateLayout: (id, patch) => {
      const state = get();
      const page = pageOf(state.history.present, state.pageId);
      if (!page) return;
      commitRoot(state, treeUpdateLayout(page.root, id, patch, state.breakpoint));
    },

    updateSelectedProps: (props) => {
      const state = get();
      const page = pageOf(state.history.present, state.pageId);
      if (!page || !state.selectedId) return;
      commitRoot(state, updateProps(page.root, state.selectedId, props));
    },

    removeSelected: () => {
      const state = get();
      const page = pageOf(state.history.present, state.pageId);
      if (!page || !state.selectedId) return;
      commitRoot(state, removeNode(page.root, state.selectedId), { selectedId: null });
    },

    duplicateSelected: () => {
      const state = get();
      const page = pageOf(state.history.present, state.pageId);
      if (!page || !state.selectedId) return;
      const orig = (page.root.children ?? []).find((c) => c.id === state.selectedId);
      if (!orig) return;
      const base = orig.layout ?? { x: 0, y: 0, w: 240, h: 120 };
      const copy: ComponentNode = {
        ...orig,
        id: newId(orig.type),
        layout: { ...base, x: base.x + 20, y: base.y + 20 },
      };
      const nextRoot = { ...page.root, children: [...(page.root.children ?? []), copy] };
      commitRoot(state, nextRoot, { selectedId: copy.id });
    },

    bringToFront: () => {
      const state = get();
      const nodes = state.currentNodes();
      const maxZ = Math.max(0, ...nodes.map((n) => n.layout?.z ?? 0));
      if (state.selectedId) state.updateLayout(state.selectedId, { z: maxZ + 1 });
    },
    sendToBack: () => {
      const state = get();
      const nodes = state.currentNodes();
      const minZ = Math.min(0, ...nodes.map((n) => n.layout?.z ?? 0));
      if (state.selectedId) state.updateLayout(state.selectedId, { z: minZ - 1 });
    },

    setBinding: (id, propPath, instanceSlug) => {
      const state = get();
      const page = pageOf(state.history.present, state.pageId);
      if (!page) return;
      const apply = (n: ComponentNode): ComponentNode => {
        if (n.id === id) {
          if (!instanceSlug) return { ...n, bindings: [] };
          // Persist the registry's contentType into the query — the published-site
          // hydrator builds the delivery URL (/api/{slug}/{contentType}) from it.
          const contentType = findRegistration(n.type)?.binding?.contentType;
          const query = contentType ? { contentType } : {};
          return { ...n, bindings: [{ propPath, source: { instanceSlug, query } }] };
        }
        return n.children ? { ...n, children: n.children.map(apply) } : n;
      };
      commitRoot(state, apply(page.root));
    },

    addPage: () => {
      const state = get();
      const def = state.history.present;
      const page = makePage();
      page.path = `/page-${def.pages.length + 1}`;
      page.title = `Page ${def.pages.length + 1}`;
      page.seo = { title: page.title };
      set({
        history: push(state.history, { ...def, pages: [...def.pages, page] }),
        pageId: page.id,
        dirty: true,
        selectedId: null,
      });
    },

    updatePageMeta: (patch) => {
      const state = get();
      const def = state.history.present;
      if (!state.pageId) return;
      const pages = def.pages.map((p) =>
        p.id === state.pageId
          ? {
              ...p,
              title: patch.title ?? p.title,
              path: patch.path ?? p.path,
              seo: {
                ...p.seo,
                title: patch.seoTitle ?? p.seo.title,
                description: patch.seoDescription ?? p.seo.description,
              },
            }
          : p,
      );
      set({ history: push(state.history, { ...def, pages }), dirty: true });
    },

    updateTheme: (patch) => {
      const state = get();
      const def = state.history.present;
      set({ history: push(state.history, { ...def, theme: { ...def.theme, ...patch } }), dirty: true });
    },

    replaceDefinition: (def) => {
      const normalized = normalize(def);
      const state = get();
      set({
        history: push(state.history, normalized),
        pageId: normalized.pages[0]?.id ?? null,
        selectedId: null,
        dirty: true,
      });
    },

    undo: () => set((s) => ({ history: undo(s.history), dirty: true, selectedId: null })),
    redo: () => set((s) => ({ history: redo(s.history), dirty: true, selectedId: null })),
    markSaved: () => set({ dirty: false }),

    definition: () => get().history.present,
    currentPage: () => pageOf(get().history.present, get().pageId),
    currentNodes: () => pageOf(get().history.present, get().pageId)?.root.children ?? [],
    canUndo: () => canUndo(get().history),
    canRedo: () => canRedo(get().history),
  };
});

export { effectiveLayout };
