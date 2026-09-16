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

/** The starter templates a new Mode B site can begin from, in the order they are offered. */
export const SITE_TEMPLATES = ['blank', 'content', 'landing'] as const;
export type SiteTemplate = (typeof SITE_TEMPLATES)[number];

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

  /**
   * A complete new site from one of the starter templates: the template's files plus this
   * tenant's generated API layer. See `SiteTemplates` on the server.
   */
  scaffold: (siteId: string, template: SiteTemplate) =>
    api.get<{ fingerprint: string; files: Record<string, string> }>(
      `/admin/sites/${siteId}/starter-files?template=${template}`,
    ),

  /**
   * Only the DCMS-owned files — `src/api/`, `src/dcms/` and `openapi.json` — generated from the
   * tenant's current plugins. Never a file the author wrote.
   */
  generatedFiles: (siteId: string) =>
    api.get<{ fingerprint: string; files: Record<string, string> }>(
      `/admin/sites/${siteId}/generated-files`,
    ),

  /** The fingerprint of what `generatedFiles` would return now — compared with the site's manifest. */
  apiFingerprint: () => api.get<{ fingerprint: string }>('/admin/api-fingerprint'),
};
