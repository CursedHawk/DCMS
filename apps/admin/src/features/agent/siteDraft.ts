import { ApiError } from '../../lib/api';
import { queryClient } from '../../lib/queryClient';
import { agentClientId } from '../site-source/clientId';
import { ideApi, type IdeState } from '../site-source/ide';
import { normalizePath } from '../site-source/paths';
import { flushDraftOf } from '../site-source/useDraftSession';
import { useVfs, type Delta } from '../site-source/vfs';
import { buildIndex, type ProjectIndex, updateIndex } from './projectIndex';
import type { WorkspacePort } from './transaction';
import { createWorkspace, type AgentWorkspace } from './workspace';

/**
 * One agent run's copy of its site (ADR 0033).
 *
 * <p>A run used to read and write the editor's store directly. That store holds one site at a
 * time and lives with the page, so a run outlived by its page wrote into whichever site was
 * loaded next — and every read came from a snapshot pinned at run start, so a run never saw its
 * own edits. This is the replacement: the run's own map, saved through the same draft API the
 * editor uses, merged back into the editor when — and only when — the editor holds this site.</p>
 *
 * <h3>Two clients of one draft</h3>
 * <p>While the editor is open on this site the author can type alongside the run. Reads take the
 * author's text for every path the run is not itself holding unsaved, so the run never works
 * from a version the author has moved past; writes go to the server with the base hash the run
 * read, so a genuine collision on one file is a per-file 409 for the run and a conflict card for
 * the author, never a silent overwrite either way.</p>
 */
export interface SiteDraft {
  readonly siteId: string;
  readonly branch: string;
  /** The write side, for the run's transaction. */
  readonly port: WorkspacePort;
  /** Every file as the run sees it now. Do not mutate. */
  files(): Readonly<Record<string, string>>;
  /** A workspace over the files as they are at this moment. Build one per tool call. */
  workspace(): AgentWorkspace;
  index(): ProjectIndex;
  /** Save everything pending now. Resolves once the server has it (or refused it). */
  flush(): Promise<void>;
  /** Messages for the model about writes the server refused. Emptied by reading. */
  takeNotes(): string[];
  /** True while a write could not be saved for a reason other than a conflict. */
  failing(): boolean;
}

export interface OpenDraftOptions {
  /** Defaults to the branch the editor holds for this site, else the site's default branch. */
  branch?: string;
  /** Names the run's saves (see `agentClientId`). */
  runId: string;
  /** Milliseconds of quiet before a batch of writes is saved. */
  debounceMs?: number;
  /** Injected by tests; the draft API otherwise. */
  api?: Pick<typeof ideApi, 'load' | 'saveFiles'>;
}

/** Retries for a save that failed on the network, before the draft reports itself failing. */
const RETRY_MS = 2000;

export async function openSiteDraft(siteId: string, options: OpenDraftOptions): Promise<SiteDraft> {
  const draftApi = options.api ?? ideApi;
  const debounceMs = options.debounceMs ?? 400;
  const client = agentClientId(options.runId);

  const held = useVfs.getState();
  const fromEditor = held.siteId === siteId && held.loaded && (!options.branch || options.branch === held.branch);

  let files: Record<string, string>;
  let base: Record<string, string>;
  let version: number;
  let branch: string;

  if (fromEditor) {
    // The author's pending text goes to the server first; otherwise it would be copied in here
    // as if it were saved, and the run's first write of that file would carry it under a stale
    // base hash.
    await flushDraftOf(siteId);
    const v = useVfs.getState();
    files = { ...v.files };
    base = { ...v.baseHashes };
    version = v.version;
    branch = v.branch;
  } else {
    const loaded = await draftApi.load(siteId, options.branch);
    files = { ...loaded.files };
    base = { ...loaded.hashes };
    version = loaded.version;
    branch = loaded.branch;
  }

  /** Writes not yet on the server: path → content, `null` for a delete. */
  const pending = new Map<string, string | null>();
  const notes: string[] = [];
  let failing = false;
  let revision = 0;
  let index: ProjectIndex | null = null;
  let seenEditorRev = -1;

  const attached = (): boolean => {
    const v = useVfs.getState();
    return v.siteId === siteId && v.loaded && v.branch === branch;
  };

  /** Fold the author's edits in, for every path the run is not holding unsaved. */
  function sync(): void {
    if (!attached()) return;
    const v = useVfs.getState();
    if (v.rev === seenEditorRev) return;
    seenEditorRev = v.rev;
    let changed = false;
    for (const [path, content] of Object.entries(v.files)) {
      if (pending.has(path)) continue;
      if (files[path] !== content) {
        files[path] = content;
        changed = true;
      }
      if (v.baseHashes[path] !== undefined) base[path] = v.baseHashes[path];
    }
    for (const path of Object.keys(files)) {
      if (v.files[path] !== undefined || pending.has(path)) continue;
      delete files[path];
      delete base[path];
      changed = true;
    }
    if (changed) {
      revision++;
      index = null;
    }
  }

  let timer: ReturnType<typeof setTimeout> | undefined;
  let chain: Promise<void> = Promise.resolve();

  function schedule(ms = debounceMs): void {
    clearTimeout(timer);
    timer = setTimeout(() => void flush(), ms);
  }

  function flush(): Promise<void> {
    clearTimeout(timer);
    chain = chain.then(saveOnce, saveOnce);
    return chain;
  }

  async function saveOnce(): Promise<void> {
    if (pending.size === 0) return;
    const batch = new Map(pending);
    const delta: Delta = { put: {}, delete: [] };
    for (const [path, content] of batch) {
      if (content === null) delta.delete.push({ path, baseHash: base[path] ?? null });
      else delta.put[path] = { content, baseHash: base[path] ?? null };
    }

    try {
      const res = await draftApi.saveFiles(siteId, branch, delta, 'agent', client);
      failing = false;
      version = Math.max(version, res.version);
      for (const [path, content] of batch) {
        // A later write to the same path stays pending; this one is done.
        if (pending.get(path) === content) pending.delete(path);
        if (content === null) delete base[path];
        else if (res.hashes[path] !== undefined) base[path] = res.hashes[path];
      }
      if (attached()) {
        const conflicts = useVfs.getState().applyAgentWrites(Object.fromEntries(batch), res.version, res.hashes);
        for (const path of conflicts) {
          notes.push(
            `${path} is also being edited by the author in the open editor. Your version is saved; they have been asked to resolve the difference. Re-read it before changing it again.`,
          );
        }
        seenEditorRev = useVfs.getState().rev;
      }
      void queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        // Nothing in the batch was written. The contested files take the server's version;
        // the rest stay pending and go with the next save.
        const contested = ((error.detail as { conflicts?: { path?: string }[] })?.conflicts ?? [])
          .map((c) => c?.path)
          .filter((p): p is string => typeof p === 'string' && p.length > 0);
        let latest: IdeState | null = null;
        try {
          latest = await draftApi.load(siteId, branch);
        } catch {
          // Reported below as a failure to save; the next attempt asks again.
        }
        for (const path of contested) {
          pending.delete(path);
          if (latest) {
            if (latest.files[path] === undefined) {
              delete files[path];
              delete base[path];
            } else {
              files[path] = latest.files[path];
              base[path] = latest.hashes[path];
            }
          }
          notes.push(
            `Your change to ${path} was NOT saved: the file changed elsewhere while you were working. It now holds that other version. Re-read it and redo your change.`,
          );
        }
        revision++;
        index = null;
        if (pending.size > 0) schedule(0);
        return;
      }
      failing = true;
      schedule(RETRY_MS);
    }
  }

  function write(path: string, content: string | null): void {
    if (content === null) delete files[path];
    else files[path] = content;
    pending.set(path, content);
    revision++;
    if (index) updateIndex(index, path, content);
    schedule();
  }

  const port: WorkspacePort = {
    read: (path) => {
      sync();
      return files[path];
    },
    exists: (path) => {
      sync();
      return files[path] !== undefined;
    },
    normalize: normalizePath,
    write: (path, content) => write(path, content),
    remove: (path) => write(path, null),
  };

  return {
    siteId,
    branch,
    port,
    files: () => {
      sync();
      return files;
    },
    workspace: () => {
      sync();
      // Copied: a tool's async body must see one version of the project even while the next
      // write lands in the map.
      return createWorkspace({ ...files }, { siteId, branch, revision }, (index ??= buildIndex(files)));
    },
    index: () => {
      sync();
      return (index ??= buildIndex(files));
    },
    flush,
    takeNotes: () => notes.splice(0),
    failing: () => failing,
  };
}
