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
  /**
   * Tabs that survive a close-others and sort to the front.
   *
   * <p>A reference file — the type you are implementing against, the route table you keep
   * checking — gets lost among the twenty files an agent run opens. Pinning is how an editor
   * says "this one stays", and it is the cheapest answer to a tab strip that churns.</p>
   */
  pinnedTabs: string[];
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
  /**
   * Paths that could not be reconciled with the server's copy.
   *
   * <p>These are QUARANTINED, not a stop signal: `takeDelta` skips them and everything else
   * keeps saving. Autosave used to halt entirely until the author reloaded, which froze the
   * whole project over one contested file.</p>
   */
  conflict: string[] | null;

  /**
   * How many agent runs are currently writing.
   *
   * <p>Autosave holds while this is above zero, and flushes once when it returns to zero. A run
   * that touches five files would otherwise produce five draft deltas, five `DraftChanged`
   * messages and five entries in the author's history — the debounce coalesces writes that land
   * within 1.2s of each other, which a run doing real work between edits routinely does not.</p>
   *
   * <p>A counter rather than a flag because runs can overlap (the dock and the IDE panel are two
   * surfaces over one workspace), and the hold must last until the last of them finishes.</p>
   */
  agentRuns: number;

  /**
   * A pending "put the caret here". `token` increments on every request so clicking the same
   * result twice reveals twice — without it the second click is a no-op, which reads as the
   * list having gone dead.
   */
  reveal: { path: string; line: number; column: number; token: number } | null;

  /** Hydrate from the server (pristine — no pending changes). */
  load: (files: Record<string, string>, version: number, hashes: Record<string, string>) => void;
  /** Seed a brand-new site from a starter template (all files pending creation). */
  seedStarter: (files: Record<string, string>) => void;
  /** Set the branch the working draft targets. */
  setBranch: (branch: string) => void;

  open: (path: string) => void;
  setActive: (path: string) => void;
  /**
   * Open a file and put the caret on a line — how a search result becomes a place in the
   * editor rather than a description of one.
   *
   * <p>State rather than a call into the editor, because the editor may not be showing the
   * file yet: the request has to survive the model swap that opening it causes. The editor
   * consumes it and calls {@link VfsState.clearReveal}.</p>
   */
  revealAt: (path: string, line: number, column?: number) => void;
  clearReveal: () => void;
  closeTab: (path: string) => void;
  togglePin: (path: string) => void;
  /** Close every unpinned tab. Pinning is what makes this safe to offer. */
  closeOthers: (keep: string | null) => void;
  /** Restore a previously persisted set of open tabs (dropping any that no longer exist). */
  restoreSession: (openTabs: string[], activePath: string | null) => void;

  /** Open (or focus) a diff tab in the editor area. */
  openDiff: (tab: DiffTab) => void;
  setActiveDiff: (path: string) => void;
  closeDiff: (path: string) => void;

  writeFile: (path: string, content: string) => void;
  createFile: (rawPath: string, content?: string) => string | null;
  /** Bulk-add uploaded files (overwrites existing, skips path-unsafe). Returns what happened. */
  importFiles: (entries: { path: string; content: string }[]) => {
    added: number;
    skipped: string[];
  };
  deleteFile: (path: string) => void;
  renameFile: (from: string, rawTo: string) => string | null;
  /** Delete every file under a folder. Returns how many were removed. */
  deleteFolder: (folder: string) => number;
  /** Move/rename a folder and everything under it. Returns the new prefix, or null. */
  renameFolder: (from: string, rawTo: string) => string | null;

  /** Snapshot the pending changes to send to the server. */
  takeDelta: () => Delta;
  /** Fold a successful save back in: advance version, update baselines, clear synced paths. */
  reconcile: (delta: Delta, version: number, hashes: Record<string, string>) => void;
  setConflict: (paths: string[]) => void;
  clearConflict: () => void;
  /**
   * Settle one conflicted file, taking `content` as the resolved text.
   *
   * <p>Pass the server's hash so the resolution saves against the version it was merged from.
   * The path leaves quarantine and rejoins the normal save flow; the others stay put.</p>
   */
  resolveConflict: (path: string, content: string, serverHash: string) => void;
  /**
   * Fold a newer server draft into this tab.
   *
   * <p>Called when the hub reports that this account's draft moved somewhere else — a second tab
   * or the agent. Files this tab has not touched are adopted silently; files it has touched are
   * compared, and only genuinely divergent ones become conflicts. Returns the paths that could
   * not be reconciled, which is what the banner is for.</p>
   */
  mergeRemote: (
    files: Record<string, string>,
    version: number,
    hashes: Record<string, string>,
  ) => string[];
  snapshot: () => Record<string, string>;

  /** Hold autosave for the duration of an agent run. See {@link VfsState.agentRuns}. */
  beginAgentRun: () => void;
  /** Release the hold. The draft session flushes when the count reaches zero. */
  endAgentRun: () => void;
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

/**
 * Normalize a folder path. Folders are not stored — they exist only as shared
 * prefixes of file paths — so this is the file rule with a tolerated trailing
 * slash.
 */
export function normalizeFolder(raw: string): string | null {
  return normalizePath(raw.replace(/\/+$/, ''));
}

/** Every file path inside `folder`, at any depth. */
function pathsUnder(files: Record<string, string>, folder: string): string[] {
  const prefix = `${folder}/`;
  return Object.keys(files).filter((p) => p.startsWith(prefix));
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
  pinnedTabs: [],
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
  reveal: null,
  // Deliberately not reset by load() or seedStarter(): a run in flight survives a branch load,
  // and zeroing the counter underneath it would release a hold the run still owns.
  agentRuns: 0,

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

  revealAt: (path, line, column = 1) =>
    set((s) => ({
      activePath: path,
      activeDiff: null,
      openTabs: s.openTabs.includes(path) ? s.openTabs : [...s.openTabs, path],
      reveal: { path, line, column, token: (s.reveal?.token ?? 0) + 1 },
    })),

  clearReveal: () => set({ reveal: null }),

  restoreSession: (openTabs, activePath) =>
    set((s) => {
      // Keep only tabs whose files still exist in the freshly loaded map.
      const tabs = openTabs.filter((p) => s.files[p] != null);
      if (tabs.length === 0) return s; // nothing to restore — keep the load() default
      const active = activePath && tabs.includes(activePath) ? activePath : tabs[tabs.length - 1];
      return { openTabs: tabs, activePath: active, activeDiff: null };
    }),

  closeTab: (path) =>
    set((s) => {
      const openTabs = s.openTabs.filter((p) => p !== path);
      const activePath =
        s.activePath === path ? (openTabs[openTabs.length - 1] ?? null) : s.activePath;
      // Closing a pinned tab unpins it. Keeping the pin would resurrect it on the next open,
      // which is a tab that reappears for reasons the author cannot see.
      return { openTabs, activePath, pinnedTabs: s.pinnedTabs.filter((p) => p !== path) };
    }),

  togglePin: (path) =>
    set((s) => ({
      pinnedTabs: s.pinnedTabs.includes(path)
        ? s.pinnedTabs.filter((p) => p !== path)
        : [...s.pinnedTabs, path],
    })),

  closeOthers: (keep) =>
    set((s) => {
      const openTabs = s.openTabs.filter((p) => p === keep || s.pinnedTabs.includes(p));
      return {
        openTabs,
        activePath: openTabs.includes(s.activePath ?? '')
          ? s.activePath
          : (openTabs[openTabs.length - 1] ?? null),
      };
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

  deleteFolder: (folder) => {
    const dir = normalizeFolder(folder);
    if (!dir) return 0;
    const victims = pathsUnder(get().files, dir).filter((p) => !isToolchainFile(p));
    if (victims.length === 0) return 0;
    const gone = new Set(victims);
    set((s) => {
      const files = { ...s.files };
      const dirtyPaths = new Set(s.dirtyPaths);
      const deletedPaths = new Set(s.deletedPaths);
      for (const p of victims) {
        delete files[p];
        dirtyPaths.delete(p);
        // Same rule as deleteFile: only a server-known file needs a delete op.
        if (s.baseHashes[p] !== undefined) deletedPaths.add(p);
      }
      const openTabs = s.openTabs.filter((p) => !gone.has(p));
      return {
        files,
        openTabs,
        activePath:
          s.activePath && gone.has(s.activePath)
            ? (openTabs[openTabs.length - 1] ?? null)
            : s.activePath,
        openDiffs: s.openDiffs.filter((d) => !gone.has(d.path)),
        activeDiff: s.activeDiff && gone.has(s.activeDiff) ? null : s.activeDiff,
        dirtyPaths,
        deletedPaths,
        dirty: dirtyPaths.size > 0 || deletedPaths.size > 0,
        rev: s.rev + 1,
      };
    });
    return victims.length;
  },

  renameFolder: (from, rawTo) => {
    const src = normalizeFolder(from);
    const dst = normalizeFolder(rawTo);
    if (!src || !dst || src === dst) return null;
    // Moving a folder inside itself would rewrite the prefix onto itself forever.
    if (dst.startsWith(`${src}/`)) return null;

    const before = get();
    const moving = pathsUnder(before.files, src);
    if (moving.length === 0) return null;

    // Resolve the whole move up front and bail as a unit: a folder that lands
    // half-moved because one child collided is worse than one that refuses.
    const remap = new Map<string, string>();
    for (const path of moving) {
      if (isToolchainFile(path)) return null;
      const to = normalizePath(`${dst}/${path.slice(src.length + 1)}`);
      if (!to || before.files[to] !== undefined) return null;
      remap.set(path, to);
    }

    set((s) => {
      const files = { ...s.files };
      const dirtyPaths = new Set(s.dirtyPaths);
      const deletedPaths = new Set(s.deletedPaths);
      for (const [oldPath, newPath] of remap) {
        files[newPath] = files[oldPath] ?? '';
        delete files[oldPath];
        dirtyPaths.delete(oldPath);
        dirtyPaths.add(newPath);
        if (s.baseHashes[oldPath] !== undefined) deletedPaths.add(oldPath);
      }
      return {
        files,
        openTabs: s.openTabs.map((p) => remap.get(p) ?? p),
        activePath: s.activePath ? (remap.get(s.activePath) ?? s.activePath) : s.activePath,
        // A diff tab is pinned to a git path, so a moved file's diff is stale.
        openDiffs: s.openDiffs.filter((d) => !remap.has(d.path)),
        activeDiff: s.activeDiff && remap.has(s.activeDiff) ? null : s.activeDiff,
        dirtyPaths,
        deletedPaths,
        dirty: true,
        rev: s.rev + 1,
      };
    });
    return dst;
  },

  takeDelta: () => {
    const s = get();
    /*
     * Conflicted paths are QUARANTINED, not a full stop.
     *
     * Autosave used to pause entirely on a 409 until the author reloaded, which meant one
     * contested file froze every other file in the project — including work that would have
     * merged without incident. Excluding just the contested paths keeps the rest flowing, so
     * the blast radius of a conflict is the file it happened in.
     *
     * They stay dirty on purpose: once resolved (see resolveConflict) the path leaves
     * quarantine and the next delta carries it.
     */
    const quarantined = new Set(s.conflict ?? []);
    const put: Record<string, PutEntry> = {};
    for (const path of s.dirtyPaths) {
      // A dirty path with no content means it was deleted after being edited; the
      // delete op covers it.
      if (s.files[path] === undefined) continue;
      if (quarantined.has(path)) continue;
      put[path] = { content: s.files[path], baseHash: s.baseHashes[path] ?? null };
    }
    const del = [...s.deletedPaths]
      .filter((path) => !quarantined.has(path))
      .map((path) => ({
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

  // Union, not replace: a second 409 on a different file must not release the first from
  // quarantine, which would let the save loop clobber it on the next flush.
  setConflict: (paths) =>
    set((s) => {
      if (paths.length === 0) return s;
      const merged = new Set([...(s.conflict ?? []), ...paths]);
      return { conflict: [...merged] };
    }),
  clearConflict: () => set({ conflict: null }),

  resolveConflict: (path, content, serverHash) =>
    set((s) => {
      const remaining = (s.conflict ?? []).filter((p) => p !== path);
      return {
        files: { ...s.files, [path]: content },
        // The resolution is now based on the server's version, so that is the baseline the
        // next save diffs against — without this the save would 409 again immediately.
        baseHashes: { ...s.baseHashes, [path]: serverHash },
        dirtyPaths: new Set(s.dirtyPaths).add(path),
        conflict: remaining.length > 0 ? remaining : null,
        dirty: true,
        rev: s.rev + 1,
      };
    }),

  mergeRemote: (remoteFiles, version, hashes) => {
    const s = get();
    {
      const files = { ...s.files };
      const baseHashes = { ...s.baseHashes };
      const dirtyPaths = new Set(s.dirtyPaths);
      const deletedPaths = new Set(s.deletedPaths);
      const conflicts: string[] = [];

      for (const [path, remote] of Object.entries(remoteFiles)) {
        const locallyDirty = dirtyPaths.has(path);
        const local = files[path];
        /*
         * Did the SERVER move for this file?
         *
         * This is the question, and getting it wrong is the whole bug class. Comparing local
         * text to remote text answers a different one — an unsaved local edit differs from the
         * server copy by definition, and calling that a conflict would flag every file the
         * author is currently typing in every time any other file is saved anywhere.
         *
         * A conflict needs both sides to have moved: the server's hash differs from the
         * baseline this tab last synced at, AND there is an unsaved local edit.
         */
        const serverMoved = baseHashes[path] !== hashes[path];

        if (!locallyDirty) {
          // Untouched here. Adopt it silently — this is the case the whole merge exists for,
          // and prompting about it is exactly the false positive being removed.
          if (local !== remote) files[path] = remote;
          baseHashes[path] = hashes[path];
          continue;
        }

        if (!serverMoved) {
          // Edited here, untouched there. An ordinary unsaved edit; the pending save still
          // applies cleanly and there is nothing to reconcile.
          continue;
        }

        if (local === remote) {
          // Both arrived at the same text. Not a conflict by any useful definition; take the
          // server's hash and drop the pending write.
          baseHashes[path] = hashes[path];
          dirtyPaths.delete(path);
          continue;
        }

        // Genuinely divergent: edited here and changed there. This is the only case a person
        // needs to see, and ConflictResolver shows both sides.
        conflicts.push(path);
      }

      // A file the server no longer has, which this tab has not touched, is gone.
      for (const path of Object.keys(files)) {
        if (remoteFiles[path] !== undefined) continue;
        if (dirtyPaths.has(path)) continue;
        delete files[path];
        delete baseHashes[path];
        deletedPaths.delete(path);
      }

      const conflict = [...new Set([...(s.conflict ?? []), ...conflicts])];
      set({
        files,
        baseHashes,
        dirtyPaths,
        deletedPaths,
        version,
        conflict: conflict.length > 0 ? conflict : null,
        dirty: dirtyPaths.size > 0 || deletedPaths.size > 0,
        rev: s.rev + 1,
        // Not a generation bump: the editor keeps its cursors and undo stack. A generation
        // change resyncs every Monaco model, which throws both away — acceptable on a branch
        // load, gratuitous when one file changed under a tab nobody was typing in.
      });
      return conflicts;
    }
  },
  beginAgentRun: () => set((s) => ({ agentRuns: s.agentRuns + 1 })),
  // Floored at zero: an endAgentRun without a matching begin (a run torn down twice by a
  // reload, say) must not leave the counter negative, which would hold autosave forever.
  endAgentRun: () => set((s) => ({ agentRuns: Math.max(0, s.agentRuns - 1) })),
  setBranch: (branch) => set({ branch }),
  snapshot: () => get().files,
}));
