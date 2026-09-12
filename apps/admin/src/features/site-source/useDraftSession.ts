import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { ApiError } from '../../lib/api';
import { ideApi } from './ide';
import { useVfs } from './vfs';

/**
 * The working-draft session shared by every git-backed site editor: load a
 * branch's draft, seed a starter when the site is brand new, and autosave
 * granular deltas with per-file conflict detection.
 *
 * The save is a delta, not a snapshot: each changed file carries the hash it was
 * last synced at, so two tabs editing different files merge and two tabs editing
 * the same file get a 409 instead of one clobbering the other.
 *
 * A conflict QUARANTINES the contested files rather than stopping autosave. It used to stop
 * everything until the author reloaded, which froze the whole project over one file — including
 * work that would have merged without incident. `takeDelta` skips quarantined paths, so the rest
 * keeps flowing and the blast radius of a conflict is the file it happened in.
 *
 * Autosave also holds for the duration of an agent run, so a run that touches five files
 * produces one save rather than five. See `VfsState.agentRuns`.
 */

export interface DraftSessionOptions {
  siteId: string;
  /**
   * Files to seed when the branch's draft is empty (a brand-new site). Return
   * null to handle seeding yourself — the Mode B IDE asks the user first.
   */
  seed?: () => Record<string, string> | null;
  /** Milliseconds of quiet before a save is flushed. */
  debounceMs?: number;
  /**
   * Called after a successful load. `isEmpty` is true for a brand-new site whose
   * draft held no files — the IDE uses it to ask which starter to scaffold.
   */
  onLoaded?: (branch: string, isEmpty: boolean) => void;
}

export interface DraftSession {
  /** True once a draft has been loaded (or seeded) for this site. */
  ready: boolean;
  /** True while a branch switch is in flight. */
  switching: boolean;
  /** True while a save request is in flight. */
  saving: boolean;
  /** Human-readable save status for the status bar. */
  status: string;
  /**
   * Load (or switch to) a branch; undefined means the site's default. Switching
   * to a different branch flushes the current one first — drafts are per-branch,
   * so unsaved work would otherwise be left behind on the branch being left.
   */
  openBranch: (branch?: string) => void;
  /** Flush pending changes immediately, e.g. before committing. */
  flush: () => Promise<void>;
  /**
   * Pull the server's newer draft and fold it in.
   *
   * <p>Called when the hub reports this account's draft moved elsewhere. Returns the paths that
   * genuinely diverged — everything else is reconciled without the author being asked.</p>
   */
  syncFromServer: () => Promise<string[]>;
}

export function useDraftSession({
  siteId,
  seed,
  debounceMs = 1200,
  onLoaded,
}: DraftSessionOptions): DraftSession {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const dirty = useVfs((s) => s.dirty);
  const rev = useVfs((s) => s.rev);
  const conflict = useVfs((s) => s.conflict);
  const agentRuns = useVfs((s) => s.agentRuns);
  const [ready, setReady] = useState(false);
  const [switching, setSwitching] = useState(false);
  const [status, setStatus] = useState('');
  const loadedFor = useRef<string | null>(null);

  const load = useMutation({
    mutationFn: (branch?: string) => ideApi.load(siteId, branch),
    onSuccess: (data) => {
      const vfs = useVfs.getState();
      vfs.setBranch(data.branch);
      const empty = Object.keys(data.files).length === 0;
      if (!empty) {
        vfs.load(data.files, data.version, data.hashes);
      } else {
        const seeded = seed?.() ?? null;
        // An empty draft with no seed is not an error: the caller (the IDE) is
        // about to ask the user what to scaffold.
        if (seeded) vfs.seedStarter(seeded);
        else vfs.load({}, data.version, data.hashes);
      }
      setReady(true);
      setSwitching(false);
      onLoaded?.(data.branch, empty);
    },
    onError: () => {
      setSwitching(false);
      toast.error(t('errors.loadFailed'));
    },
  });

  /**
   * Fetch the server's draft and merge it into this tab.
   *
   * <p>The merge rules live in the store (`mergeRemote`); this is only the fetch. Files the tab
   * has not touched are adopted silently, files that happen to match are reconciled, and only
   * real divergence is reported back.</p>
   */
  const pull = useCallback(async (): Promise<string[]> => {
    try {
      const data = await ideApi.load(siteId, useVfs.getState().branch);
      return useVfs.getState().mergeRemote(data.files, data.version, data.hashes);
    } catch {
      // A failed pull leaves the tab exactly as it was, which is safe: the local work is intact
      // and the next save either succeeds or re-reports the conflict.
      return [];
    }
  }, [siteId]);

  const save = useMutation({
    mutationFn: async () => {
      const vfs = useVfs.getState();
      const delta = vfs.takeDelta();
      if (Object.keys(delta.put).length === 0 && delta.delete.length === 0) return;
      const res = await ideApi.saveFiles(siteId, vfs.branch, delta);
      if (res) useVfs.getState().reconcile(delta, res.version, res.hashes);
    },
    onSuccess: () => {
      setStatus(t('common.saved'));
      // The Source Control panel's changed-file list is derived from the draft.
      queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
    },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 409) {
        // Filtered, because a malformed entry is worse than a dropped one: `[undefined]` is a
        // truthy array, so it raises the banner and then names nothing in it — "changed these
        // files while you were editing: ." — while also quarantining a path that does not
        // exist, which silently excludes nothing from every later delta.
        const paths = ((e.detail as { conflicts?: { path: string }[] })?.conflicts ?? [])
          .map((c) => c?.path)
          .filter((p): p is string => typeof p === 'string' && p.length > 0);
        /*
         * Quarantine the contested files and keep going.
         *
         * Autosave used to stop dead here until the author reloaded, which froze every other
         * file in the project over one contested one — including work that would have merged
         * without incident. `takeDelta` now skips quarantined paths, so the rest keeps
         * flowing and the blast radius of a conflict is the file it happened in.
         *
         * The pull that follows is what turns a bare "conflict" into something reviewable: it
         * fetches the server's text so ConflictResolver has both sides to show.
         */
        useVfs.getState().setConflict(paths);
        void pull();
      } else {
        toast.error(t('errors.generic'));
      }
    },
  });

  const flush = useCallback(async () => {
    // No longer refuses outright on conflict: the delta excludes quarantined paths, so a flush
    // during an unresolved conflict still saves everything that is not contested.
    await save.mutateAsync();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const openBranch = useCallback(
    (branch?: string) => {
      setSwitching(true);
      const current = useVfs.getState();
      const leaving = branch !== undefined && branch !== current.branch;

      if (!leaving || !current.dirty) {
        load.mutate(branch);
        return;
      }

      // Save what is pending on the branch being left before loading the next
      // one. A failure is not fatal — the draft stays server-side as it was, and
      // blocking the switch would trap the author on a branch they want to leave.
      void flush()
        .catch(() => undefined)
        .then(() => load.mutate(branch));
    },
    // `load` is a stable mutation object from react-query.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [siteId, flush],
  );

  // Load once per site.
  useEffect(() => {
    if (loadedFor.current === siteId) return;
    loadedFor.current = siteId;
    setReady(false);
    openBranch(undefined);
  }, [siteId, openBranch]);

  /*
   * Debounced autosave, paused while a conflict is unresolved or an agent run is writing.
   *
   * The agent hold is what makes a multi-file change one save rather than one per file. The
   * debounce alone does not achieve that: it coalesces writes within 1.2s of each other, and a
   * run that reads, thinks and validates between edits routinely takes longer than that per
   * file. Without the hold a five-file change is five deltas, five `DraftChanged` messages to
   * every other tab, and five things for the author to review.
   *
   * `agentRuns` returning to zero is itself a dependency, so the flush happens on release
   * without needing the run to ask for it — which matters because a run that throws still has
   * its edits in the workspace, and they still need saving.
   */
  useEffect(() => {
    // `conflict` is deliberately NOT a reason to stop: the delta already excludes quarantined
    // paths, so continuing saves everything that is not contested. It stays a dependency so a
    // newly-quarantined path re-triggers the effect and the remaining work flushes promptly.
    if (!ready || !dirty || agentRuns > 0) return;
    const id = setTimeout(() => save.mutate(), debounceMs);
    return () => clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rev, dirty, conflict, ready, debounceMs, agentRuns]);

  return {
    ready,
    switching,
    saving: save.isPending,
    status,
    openBranch,
    flush,
    syncFromServer: pull,
  };
}
