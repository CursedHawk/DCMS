import { api } from '../../lib/api';

// Git backing for a Mode B site: the source lives in a Forgejo repo (one per site).
// Status auto-provisions the repo on first read, so opening the IDE is enough.

export interface GitStatus {
  enabled: boolean;
  provisioned?: boolean;
  repo?: string;
  branch?: string;
  httpUrl?: string;
  sshUrl?: string;
  provisionedAt?: string | null;
}

export interface GitBranch {
  name: string;
  sha?: string | null;
}

export interface GitCommit {
  sha: string;
  shortSha: string;
  message?: string | null;
  author?: string | null;
  date?: string | null;
  htmlUrl?: string | null;
  avatar?: string | null;
}

export type GitChangeStatus = 'added' | 'modified' | 'deleted';

export interface GitChange {
  path: string;
  status: GitChangeStatus;
  headContent: string | null;
  draftContent: string | null;
  truncated: boolean;
}

export interface CommitBody {
  branch: string;
  message: string;
  description?: string;
  targetBranch?: string;
  newBranch?: string;
  // How to resolve files changed on both sides when the branch moved: keep the
  // draft's version ("mine") or the branch's ("theirs"). Omitted on the first try.
  resolve?: 'mine' | 'theirs';
}

export interface GitCompare {
  totalCommits: number;
  files: { path: string; status: string }[];
}

export interface MergeConflictFile {
  path: string;
  /** The base/target-branch version ("ours"). */
  releaseContent: string | null;
  /** The incoming version ("theirs"). */
  branchContent: string | null;
  /** The common ancestor, for a 3-way view (may be absent). */
  baseContent?: string | null;
}

export interface MergeResult {
  merged: boolean;
  upToDate?: boolean;
  sha?: string | null;
  conflict?: boolean;
  files?: MergeConflictFile[];
}

export type GitBuildStatus = 'Queued' | 'Building' | 'Succeeded' | 'Failed';

export interface GitBuild {
  id: string;
  status: GitBuildStatus;
  gitCommitSha?: string | null;
  shortSha?: string | null;
  error?: string | null;
  hasLog: boolean;
  createdAt: string;
  completedAt?: string | null;
  active: boolean;
}

export const gitApi = {
  status: (siteId: string) => api.get<GitStatus>(`/admin/sites/${siteId}/git`),
  branches: (siteId: string) => api.get<GitBranch[]>(`/admin/sites/${siteId}/git/branches`),
  history: (siteId: string, branch?: string, limit = 50) => {
    const qs = new URLSearchParams({ limit: String(limit) });
    if (branch) qs.set('branch', branch);
    return api.get<GitCommit[]>(`/admin/sites/${siteId}/git/history?${qs}`);
  },
  // Working draft vs branch HEAD — the source-control changes list + diffs.
  changes: (siteId: string, branch: string) =>
    api.get<GitChange[]>(`/admin/sites/${siteId}/git/changes?branch=${encodeURIComponent(branch)}`),
  // Commit the working draft to a branch (or a new/other branch). Author = the user.
  commit: (siteId: string, body: CommitBody) =>
    api.post<{ sha: string; branch: string }>(`/admin/sites/${siteId}/git/commit`, body),
  // Create a branch off `from` (defaults server-side to the site's default branch).
  createBranch: (siteId: string, name: string, from?: string) =>
    api.post<{ name: string; from: string }>(`/admin/sites/${siteId}/git/branches`, { name, from }),
  // Restore a commit's tree into the working draft on a branch (does not commit).
  restore: (siteId: string, sha: string, branch: string) =>
    api.post<{
      branch: string;
      version: number;
      files: Record<string, string>;
      hashes: Record<string, string>;
    }>(`/admin/sites/${siteId}/git/restore?branch=${encodeURIComponent(branch)}`, { sha }),
  // Pre-merge review: what merging `head` into `base` (default release) would bring.
  compare: (siteId: string, head: string, base?: string) => {
    const qs = new URLSearchParams({ head });
    if (base) qs.set('base', base);
    return api.get<GitCompare>(`/admin/sites/${siteId}/git/compare?${qs}`);
  },
  // Merge `head` into `base` (default release). Clean → { merged, sha }; conflict → { conflict, files }.
  merge: (siteId: string, head: string, base?: string) =>
    api.post<MergeResult>(`/admin/sites/${siteId}/git/merge`, { head, base }),
  // Complete a conflicted merge with per-file resolved content (null = delete).
  resolveMerge: (
    siteId: string,
    head: string,
    resolutions: Record<string, string | null>,
    base?: string,
  ) =>
    api.post<MergeResult>(`/admin/sites/${siteId}/git/merge/resolve`, { head, base, resolutions }),
  // Recent builds (deployments) for a site, newest first.
  builds: (siteId: string, limit = 20) =>
    api.get<GitBuild[]>(`/admin/sites/${siteId}/builds?limit=${limit}`),
  // Full build log (install + build output) as plain text.
  buildLog: (siteId: string, buildId: string) =>
    api.get<string>(`/admin/sites/${siteId}/builds/${buildId}/log`),
  // Force a fresh build+deploy of the current release head (recovers a failed/wedged build).
  rebuild: (siteId: string) => api.post<{ buildId: string }>(`/admin/sites/${siteId}/builds`, {}),
};
