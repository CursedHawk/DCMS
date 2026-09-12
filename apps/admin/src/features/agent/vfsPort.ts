import { normalizePath } from '../site-source/paths';
import { useVfs } from '../site-source/vfs';
import { buildIndex, type ProjectIndex, updateIndex } from './projectIndex';
import type { WorkspacePort } from './transaction';
import { createWorkspace } from './workspace';

/**
 * Binds the agent's workspace abstraction to the real editor state.
 *
 * <p>This is the seam where "the agent is another client of the same workspace" stops being a
 * design statement and becomes a fact: every read below comes from the same zustand store the
 * editor renders, and every write goes through the same mutators the author's keystrokes use. An
 * agent edit lands in the open tab, bumps `rev`, and rebuilds the preview by exactly the same
 * path a human edit does — there is no second channel that could drift.</p>
 */

/**
 * The project index for the current file map.
 *
 * <p>Kept module-level and updated incrementally rather than rebuilt per run. A full build is
 * milliseconds, but it is milliseconds on every tool call, and the incremental path re-scans the
 * one file that changed.</p>
 */
let index: ProjectIndex | null = null;
let indexedRev = -1;
let indexedFileCount = -1;

/**
 * The index for the current workspace, built or refreshed as needed.
 *
 * <p>Rebuilt wholesale when the file <i>count</i> changes or the revision moved backwards — a
 * branch load, a starter seed, an undo — because those are the cases where tracking individual
 * writes cannot catch up. Otherwise the caller is expected to have reported its own edits
 * through {@link noteIndexedChange}, which is what keeps the common path cheap.</p>
 */
export function currentIndex(): ProjectIndex {
  const { files, rev } = useVfs.getState();
  const count = Object.keys(files).length;
  if (!index || rev < indexedRev || count !== indexedFileCount) {
    index = buildIndex(files);
    indexedRev = rev;
    indexedFileCount = count;
  }
  return index;
}

/** Fold one applied edit into the index without a full rebuild. */
export function noteIndexedChange(path: string, content: string | null): void {
  if (!index) return;
  updateIndex(index, path, content);
  const { rev, files } = useVfs.getState();
  indexedRev = rev;
  indexedFileCount = Object.keys(files).length;
}

/**
 * A read-only workspace pinned to the store's current state.
 *
 * <p>The file map is copied, not referenced. A run's tool calls are async and the author keeps
 * typing; a workspace that read through to the live store would return different content to two
 * calls in the same turn and make every hash guard meaningless.</p>
 */
export function currentWorkspace(siteId: string) {
  const { files, branch, rev } = useVfs.getState();
  return createWorkspace({ ...files }, { siteId, branch, revision: rev }, currentIndex());
}

/**
 * The write port over the VFS.
 *
 * <p>Note what is absent: any notion of a "platform-managed" file. That check used to live here
 * and in the IDE's tool layer, refusing edits to `package.json` and the lockfile — but
 * `TOOLCHAIN_FILES` has been empty since Mode B sites started owning their own dependencies, and
 * the backend accepts whatever the site commits (see the path-safety-only check in
 * `SiteEndpoints`). The guard was dead code that read as policy. Path safety is the real
 * constraint and `normalizePath` is where it lives.</p>
 */
export function vfsPort(): WorkspacePort {
  return {
    read: (path) => useVfs.getState().files[path],
    exists: (path) => useVfs.getState().files[path] !== undefined,
    normalize: normalizePath,
    write: (path, content) => {
      useVfs.getState().writeFile(path, content);
      // Opening the file is what makes an agent edit visible rather than mysterious: the author
      // sees the tab appear and the diff in it, instead of discovering the change at review time.
      useVfs.getState().open(path);
      noteIndexedChange(path, content);
    },
    remove: (path) => {
      useVfs.getState().deleteFile(path);
      noteIndexedChange(path, null);
    },
  };
}
