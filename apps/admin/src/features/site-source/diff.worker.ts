import { diffStat, type DiffStat } from '@dcms/gjs-parse/diff';

/**
 * Line-count diffs for the Source Control changes list.
 *
 * A working draft can hold a few hundred KB of changed files, and the list wants
 * a count for every one of them at once — on the main thread that is a visible
 * stall every time the panel refreshes, on a panel the author opens constantly.
 *
 * The whole batch is done in one message rather than one per file: the transfer
 * cost dominates the arithmetic for small files, so the round trips are the part
 * worth avoiding.
 */

export interface DiffWorkerRequest {
  id: number;
  files: { path: string; original: string | null; modified: string | null }[];
}

export interface DiffWorkerResponse {
  id: number;
  stats: Record<string, DiffStat>;
}

self.onmessage = (event: MessageEvent<DiffWorkerRequest>) => {
  const { id, files } = event.data;
  const stats: Record<string, DiffStat> = {};
  for (const file of files) {
    stats[file.path] = diffStat(file.original, file.modified);
  }
  self.postMessage({ id, stats } satisfies DiffWorkerResponse);
};
