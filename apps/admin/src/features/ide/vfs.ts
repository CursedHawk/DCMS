import { create } from 'zustand';
import { isToolchainFile, normalizePath } from './paths';

// The IDE's virtual file system. It mirrors the Mode B backend shape exactly:
// Site.DraftDefinitionJson === { files: { <path>: <content> } }. This store is
// the single source of truth; Monaco models are kept in sync with it.
//
// Saves are granular (a delta of changed/deleted files), not a whole-map replace,
// so two people editing different files no longer clobber each other. Each file
// carries the server hash it was last synced at (`baseHashes`); a save echoes that
// hash back and the server reports a conflict if the stored file changed meanwhile.

/** One file's pending write in a save delta (baseHash === null means "new file"). */
export interface PutEntry {
  content: string;
  baseHash: string | null;
}

/** The set of changes to flush to the server since the last successful save. */
export interface Delta {
  put: Record<string, PutEntry>;
  delete: { path: string; baseHash: string | null }[];
}

/** An open diff tab: a file's branch-HEAD version vs the working draft. */
export interface DiffTab {
  path: string;
  status: 'added' | 'modified' | 'deleted';
  original: string | null;
  modified: string | null;
}

interface VfsState {
  files: Record<string, string>;
  /** The git branch the working draft targets (drives the /ide + save URLs). */
  branch: string;
  openTabs: string[];
  activePath: string | null;
  /** Open diff tabs (branch HEAD vs draft), shown alongside file tabs. */
  openDiffs: DiffTab[];
  /** The active diff tab's path, or null when a normal file editor is shown. */
  activeDiff: string | null;
  /** True when there are unsaved local changes (put or delete) pending. */
  dirty: boolean;
  loaded: boolean;
  /** Bumped on every content change — the preview subscribes to this. */
  rev: number;
  /** Bumped only on a full (re)load — the editor resyncs model contents when it changes. */
  generation: number;

  /** Server definition version last seen (informational; bumps on every save). */
  version: number;
  /** Per-path server content hash last synced — the baseline a save is diffed against. */
  baseHashes: Record<string, string>;
  /** Paths created/modified locally since the last sync. */
  dirtyPaths: Set<string>;
  /** Server-known paths deleted locally since the last sync. */
  deletedPaths: Set<string>;
  /** Paths the server reported as conflicting; autosave pauses until reload. */
  conflict: string[] | null;

  /** Hydrate from the server (pristine — no pending changes). */
  load: (files: Record<string, string>, version: number, hashes: Record<string, string>) => void;
  /** Seed a brand-new site from a starter template (all files pending creation). */
  seedStarter: (files: Record<string, string>) => void;
  /** Set the branch the working draft targets. */
  setBranch: (branch: string) => void;

  open: (path: string) => void;
  setActive: (path: string) => void;
  closeTab: (path: string) => void;

  /** Open (or focus) a diff tab in the editor area. */
  openDiff: (tab: DiffTab) => void;
  setActiveDiff: (path: string) => void;
  closeDiff: (path: string) => void;

  writeFile: (path: string, content: string) => void;
  createFile: (rawPath: string, content?: string) => string | null;
  /** Bulk-add uploaded files (overwrites existing, skips toolchain). Returns what happened. */
  importFiles: (entries: { path: string; content: string }[]) => { added: number; skipped: string[] };
  deleteFile: (path: string) => void;
  renameFile: (from: string, rawTo: string) => string | null;

  /** Snapshot the pending changes to send to the server. */
  takeDelta: () => Delta;
  /** Fold a successful save back in: advance version, update baselines, clear synced paths. */
  reconcile: (delta: Delta, version: number, hashes: Record<string, string>) => void;
  setConflict: (paths: string[]) => void;
  clearConflict: () => void;
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

/** Pick the file to focus on load: prefer src/App.tsx, else the first source-ish file. */
function firstFile(files: Record<string, string>): string | null {
  return files['src/App.tsx'] != null
    ? 'src/App.tsx'
    : (Object.keys(files).find((p) => /\.(tsx?|jsx?|css|html)$/.test(p)) ?? null);
}

export const useVfs = create<VfsState>((set, get) => ({
  files: {},
  branch: 'main',
  openTabs: [],
  activePath: null,
  openDiffs: [],
  activeDiff: null,
  dirty: false,
  loaded: false,
  rev: 0,
  generation: 0,
  version: 0,
  baseHashes: {},
  dirtyPaths: new Set(),
  deletedPaths: new Set(),
  conflict: null,

  load: (files, version, hashes) => {
    const first = firstFile(files);
    set({
      files,
      version,
      baseHashes: hashes,
      dirtyPaths: new Set(),
      deletedPaths: new Set(),
      conflict: null,
      loaded: true,
      dirty: false,
      rev: get().rev + 1,
      generation: get().generation + 1,
      openTabs: first ? [first] : [],
      activePath: first,
      openDiffs: [],
      activeDiff: null,
    });
  },

  seedStarter: (files) => {
    const first = firstFile(files);
    set({
      files,
      version: 0,
      baseHashes: {},
      // Nothing exists server-side yet, so every starter file is a pending create.
      dirtyPaths: new Set(Object.keys(files)),
      deletedPaths: new Set(),
      conflict: null,
      loaded: true,
      dirty: Object.keys(files).length > 0,
      rev: get().rev + 1,
      generation: get().generation + 1,
      openTabs: first ? [first] : [],
      activePath: first,
      openDiffs: [],
      activeDiff: null,
    });
  },

  open: (path) =>
    set((s) => ({
      activePath: path,
      activeDiff: null,
      openTabs: s.openTabs.includes(path) ? s.openTabs : [...s.openTabs, path],
    })),

  setActive: (path) => set({ activePath: path, activeDiff: null }),

  closeTab: (path) =>
    set((s) => {
      const openTabs = s.openTabs.filter((p) => p !== path);
      const activePath =
        s.activePath === path ? (openTabs[openTabs.length - 1] ?? null) : s.activePath;
      return { openTabs, activePath };
    }),

  openDiff: (tab) =>
    set((s) => ({
      openDiffs: s.openDiffs.some((d) => d.path === tab.path)
        ? s.openDiffs.map((d) => (d.path === tab.path ? tab : d))
        : [...s.openDiffs, tab],
      activeDiff: tab.path,
    })),

  setActiveDiff: (path) => set({ activeDiff: path }),

  closeDiff: (path) =>
    set((s) => ({
      openDiffs: s.openDiffs.filter((d) => d.path !== path),
      activeDiff: s.activeDiff === path ? null : s.activeDiff,
    })),

  writeFile: (path, content) =>
    set((s) => {
      if (s.files[path] === content) return s;
      const dirtyPaths = new Set(s.dirtyPaths).add(path);
      const deletedPaths = s.deletedPaths.has(path)
        ? new Set([...s.deletedPaths].filter((p) => p !== path))
        : s.deletedPaths;
      return {
        files: { ...s.files, [path]: content },
        dirtyPaths,
        deletedPaths,
        dirty: true,
        rev: s.rev + 1,
      };
    }),

  createFile: (rawPath, content = '') => {
    const path = normalizePath(rawPath);
    if (!path || get().files[path] != null || isToolchainFile(path)) return null;
    set((s) => ({
      files: { ...s.files, [path]: content },
      dirtyPaths: new Set(s.dirtyPaths).add(path),
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
      const dirtyPaths = new Set(s.dirtyPaths);
      for (const p of paths) dirtyPaths.add(p);
      return {
        files: { ...s.files, ...additions },
        dirtyPaths,
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
      const dirtyPaths = new Set(s.dirtyPaths);
      dirtyPaths.delete(path);
      const deletedPaths = new Set(s.deletedPaths);
      // Only a file the server knows about needs a delete op; a local-only file
      // just disappears.
      if (s.baseHashes[path] !== undefined) deletedPaths.add(path);
      return {
        files,
        openTabs,
        activePath: s.activePath === path ? (openTabs[openTabs.length - 1] ?? null) : s.activePath,
        dirtyPaths,
        deletedPaths,
        dirty: dirtyPaths.size > 0 || deletedPaths.size > 0,
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
      const dirtyPaths = new Set(s.dirtyPaths);
      dirtyPaths.delete(from);
      dirtyPaths.add(to);
      const deletedPaths = new Set(s.deletedPaths);
      if (s.baseHashes[from] !== undefined) deletedPaths.add(from);
      return {
        files,
        openTabs: s.openTabs.map((p) => (p === from ? to : p)),
        activePath: s.activePath === from ? to : s.activePath,
        dirtyPaths,
        deletedPaths,
        dirty: true,
        rev: s.rev + 1,
      };
    });
    return to;
  },

  takeDelta: () => {
    const s = get();
    const put: Record<string, PutEntry> = {};
    for (const path of s.dirtyPaths) {
      // A dirty path with no content means it was deleted after being edited; the
      // delete op covers it.
      if (s.files[path] === undefined) continue;
      put[path] = { content: s.files[path], baseHash: s.baseHashes[path] ?? null };
    }
    const del = [...s.deletedPaths].map((path) => ({
      path,
      baseHash: s.baseHashes[path] ?? null,
    }));
    return { put, delete: del };
  },

  reconcile: (delta, version, hashes) =>
    set((s) => {
      const baseHashes = { ...s.baseHashes };
      const dirtyPaths = new Set(s.dirtyPaths);
      const deletedPaths = new Set(s.deletedPaths);
      for (const [path, entry] of Object.entries(delta.put)) {
        // The server now stores entry.content, so that is the new baseline even if
        // the user has since edited again (which keeps the path dirty for a resend).
        if (hashes[path] !== undefined) baseHashes[path] = hashes[path];
        if (s.files[path] === entry.content) dirtyPaths.delete(path);
      }
      for (const { path } of delta.delete) {
        delete baseHashes[path];
        if (s.files[path] === undefined) deletedPaths.delete(path);
        else dirtyPaths.add(path); // recreated during the in-flight save
      }
      return {
        version,
        baseHashes,
        dirtyPaths,
        deletedPaths,
        dirty: dirtyPaths.size > 0 || deletedPaths.size > 0,
      };
    }),

  setConflict: (paths) => set({ conflict: paths.length > 0 ? paths : null }),
  clearConflict: () => set({ conflict: null }),
  setBranch: (branch) => set({ branch }),
  snapshot: () => get().files,
}));
