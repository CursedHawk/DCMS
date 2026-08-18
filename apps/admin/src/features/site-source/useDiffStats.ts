import type { DiffStat } from '@dcms/gjs-parse/diff';
import { useEffect, useRef, useState } from 'react';
import DiffWorker from './diff.worker?worker';
import type { DiffWorkerRequest, DiffWorkerResponse } from './diff.worker';
import type { GitChange } from './git';

/**
 * `+12 −3` per changed file, computed off the main thread.
 *
 * The worker is created on first use and torn down with the panel: the changes
 * list is not always on screen, and a live worker per site would outlive it.
 */
export function useDiffStats(changes: readonly GitChange[]): Record<string, DiffStat> {
  const [stats, setStats] = useState<Record<string, DiffStat>>({});
  const workerRef = useRef<Worker | null>(null);
  const requestRef = useRef(0);

  useEffect(() => {
    return () => {
      workerRef.current?.terminate();
      workerRef.current = null;
    };
  }, []);

  // The identity of `changes` is a fresh array on every query refetch, so the
  // effect keys on the content that actually decides the answer.
  const signature = changes
    .map((c) => `${c.path}:${c.status}:${c.headContent?.length ?? -1}:${c.draftContent?.length ?? -1}`)
    .join('|');

  useEffect(() => {
    if (changes.length === 0) {
      setStats({});
      return;
    }

    if (!workerRef.current) {
      const worker = new DiffWorker();
      worker.onmessage = (event: MessageEvent<DiffWorkerResponse>) => {
        // Drop a reply for a batch that has since been superseded; its counts
        // belong to files the list may no longer show.
        if (event.data.id !== requestRef.current) return;
        setStats(event.data.stats);
      };
      workerRef.current = worker;
    }

    const id = ++requestRef.current;
    workerRef.current.postMessage({
      id,
      files: changes.map((c) => ({
        path: c.path,
        original: c.headContent,
        modified: c.draftContent,
      })),
    } satisfies DiffWorkerRequest);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signature]);

  return stats;
}
