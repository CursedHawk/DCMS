import { api } from '../../lib/api';
import { clientId } from './clientId';
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

  /**
   * Granular save of the working draft (per-file hash conflict → 409).
   *
   * <p>`clientId` identifies this tab so the `DraftChanged` broadcast that follows can be
   * recognised as an echo of this very save rather than as somebody else's work. `origin` says
   * whether a person or the agent wrote it, so the editor can present a run's edits as a change
   * set instead of as files mutating on their own.</p>
   */
  saveFiles: (siteId: string, branch: string, delta: Delta, origin: 'user' | 'agent' = 'user') =>
    api.patch<{ version: number; hashes: Record<string, string> }>(
      `/admin/sites/${siteId}/ide/files${branchQuery(branch)}`,
      { put: delta.put, delete: delta.delete, clientId: clientId(), origin },
    ),

  /** Tenant-generated starter files for a new React app (see StarterFlavor). */
  scaffold: (siteId: string, flavor: 'openapi' | 'client' | 'starter') =>
    api.get<{ files: Record<string, string> }>(
      `/admin/sites/${siteId}/starter-files?flavor=${flavor}`,
    ),

  /**
   * Re-emit only the DCMS-owned files — `openapi.json` and the typed client under
   * `src/api/` — from the tenant's current content API. Used to pull in plugins
   * installed or reconfigured since the site was scaffolded, without touching a
   * line the author wrote.
   */
  regenerate: (siteId: string) =>
    api.get<{ files: Record<string, string> }>(
      `/admin/sites/${siteId}/starter-files?flavor=regenerate`,
    ),
};
