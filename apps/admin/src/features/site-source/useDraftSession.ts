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
 * the same file get a 409 instead of one clobbering the other. Autosave pauses
 * entirely while a conflict is unresolved — continuing would keep resending a
 * delta the server has already rejected.
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
        const paths = ((e.detail as { conflicts?: { path: string }[] })?.conflicts ?? []).map(
          (c) => c.path,
        );
        useVfs.getState().setConflict(paths);
      } else {
        toast.error(t('errors.generic'));
      }
    },
  });

  const flush = useCallback(async () => {
    if (useVfs.getState().conflict) return;
    await save.mutateAsync();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const openBranch = useCallback(
    (branch?: string) => {
      setSwitching(true);
      const current = useVfs.getState();
      const leaving = branch !== undefined && branch !== current.branch;

      if (!leaving || !current.dirty || current.conflict) {
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

  // Debounced autosave, paused while a conflict is unresolved.
  useEffect(() => {
    if (!ready || !dirty || conflict) return;
    const id = setTimeout(() => save.mutate(), debounceMs);
    return () => clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rev, dirty, conflict, ready, debounceMs]);

  return { ready, switching, saving: save.isPending, status, openBranch, flush };
}
