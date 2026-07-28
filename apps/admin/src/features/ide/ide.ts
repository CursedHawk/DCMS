import { api } from '../../lib/api';
import type { Delta } from './vfs';

// The Mode B IDE working-draft API. Drafts are per (site, user, branch): the
// "being worked on" version that autosaves flush into, decoupled from git commits.

export interface IdeState {
  branch: string;
  baseSha: string | null;
  version: number;
  files: Record<string, string>;
  hashes: Record<string, string>;
}

function branchQuery(branch?: string): string {
  return branch ? `?branch=${encodeURIComponent(branch)}` : '';
}

export const ideApi = {
  /** Load (seeding from git HEAD on first open) the user's draft for a branch. */
  load: (siteId: string, branch?: string) =>
    api.get<IdeState>(`/admin/sites/${siteId}/ide${branchQuery(branch)}`),

  /** Granular save of the working draft (per-file hash conflict → 409). */
  saveFiles: (siteId: string, branch: string, delta: Delta) =>
    api.patch<{ version: number; hashes: Record<string, string> }>(
      `/admin/sites/${siteId}/ide/files${branchQuery(branch)}`,
      { put: delta.put, delete: delta.delete },
    ),
};
