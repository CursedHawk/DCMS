import { create } from 'zustand';
import { isToolchainFile, normalizePath } from './paths';

// The IDE's virtual file system. It mirrors the Mode B backend shape exactly:
// Site.DraftDefinitionJson === { files: { <path>: <content> } }. This store is
// the single source of truth; Monaco models are kept in sync with it.

interface VfsState {
  files: Record<string, string>;
  openTabs: string[];
  activePath: string | null;
  /** True when the map differs from what the server last stored. */
  dirty: boolean;
  loaded: boolean;
  /** Bumped on every content change — the preview subscribes to this. */
  rev: number;

  load: (files: Record<string, string>) => void;
  open: (path: string) => void;
  setActive: (path: string) => void;
  closeTab: (path: string) => void;

  writeFile: (path: string, content: string) => void;
  createFile: (rawPath: string, content?: string) => string | null;
  /** Bulk-add uploaded files (overwrites existing, skips toolchain). Returns what happened. */
  importFiles: (entries: { path: string; content: string }[]) => { added: number; skipped: string[] };
  deleteFile: (path: string) => void;
  renameFile: (from: string, rawTo: string) => string | null;

  markSaved: () => void;
  snapshot: () => Record<string, string>;
}

/** Parse the backend `definition` into a flat file map (tolerates an empty/new site). */
export function filesFromDefinition(definition: unknown): Record<string, string> {
  const files = (definition as { files?: unknown })?.files;
  if (files && typeof files === 'object' && !Array.isArray(files)) {
    const out: Record<string, string> = {};
    for (const [k, v] of Object.entries(files as Record<string, unknown>)) {
      out[k] = typeof v === 'string' ? v : String(v ?? '');
    }
    return out;
  }
  return {};
}

export const useVfs = create<VfsState>((set, get) => ({
  files: {},
  openTabs: [],
  activePath: null,
  dirty: false,
  loaded: false,
  rev: 0,

  load: (files) => {
    const first =
      files['src/App.tsx'] != null
        ? 'src/App.tsx'
        : Object.keys(files).find((p) => /\.(tsx?|jsx?|css|html)$/.test(p)) ?? null;
    set({
      files,
      loaded: true,
      dirty: false,
      rev: get().rev + 1,
      openTabs: first ? [first] : [],
      activePath: first,
    });
  },

  open: (path) =>
    set((s) => ({
      activePath: path,
      openTabs: s.openTabs.includes(path) ? s.openTabs : [...s.openTabs, path],
    })),

  setActive: (path) => set({ activePath: path }),

  closeTab: (path) =>
    set((s) => {
      const openTabs = s.openTabs.filter((p) => p !== path);
      const activePath =
        s.activePath === path ? (openTabs[openTabs.length - 1] ?? null) : s.activePath;
      return { openTabs, activePath };
    }),

  writeFile: (path, content) =>
    set((s) => {
      if (s.files[path] === content) return s;
      return { files: { ...s.files, [path]: content }, dirty: true, rev: s.rev + 1 };
    }),

  createFile: (rawPath, content = '') => {
    const path = normalizePath(rawPath);
    if (!path || get().files[path] != null || isToolchainFile(path)) return null;
    set((s) => ({
      files: { ...s.files, [path]: content },
      dirty: true,
      rev: s.rev + 1,
      openTabs: [...s.openTabs, path],
      activePath: path,
    }));
    return path;
  },

  importFiles: (entries) => {
    const skipped: string[] = [];
    const additions: Record<string, string> = {};
    for (const { path: raw, content } of entries) {
      const path = normalizePath(raw);
      if (!path || isToolchainFile(path)) {
        skipped.push(raw);
        continue;
      }
      additions[path] = content;
    }
    const paths = Object.keys(additions);
    if (paths.length === 0) return { added: 0, skipped };
    set((s) => {
      // Focus the last upload; the editor area renders a viewer for binary files.
      const active = paths[paths.length - 1];
      return {
        files: { ...s.files, ...additions },
        dirty: true,
        rev: s.rev + 1,
        openTabs: s.openTabs.includes(active) ? s.openTabs : [...s.openTabs, active],
        activePath: active,
      };
    });
    return { added: paths.length, skipped };
  },

  deleteFile: (path) => {
    if (isToolchainFile(path)) return;
    set((s) => {
      const files = { ...s.files };
      delete files[path];
      const openTabs = s.openTabs.filter((p) => p !== path);
      return {
        files,
        openTabs,
        activePath: s.activePath === path ? (openTabs[openTabs.length - 1] ?? null) : s.activePath,
        dirty: true,
        rev: s.rev + 1,
      };
    });
  },

  renameFile: (from, rawTo) => {
    const to = normalizePath(rawTo);
    if (!to || isToolchainFile(from) || isToolchainFile(to) || get().files[to] != null) return null;
    set((s) => {
      const files = { ...s.files };
      files[to] = files[from] ?? '';
      delete files[from];
      return {
        files,
        openTabs: s.openTabs.map((p) => (p === from ? to : p)),
        activePath: s.activePath === from ? to : s.activePath,
        dirty: true,
        rev: s.rev + 1,
      };
    });
    return to;
  },

  markSaved: () => set({ dirty: false }),
  snapshot: () => get().files,
}));
