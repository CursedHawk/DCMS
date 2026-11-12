import type { BuildProblem } from './problems';

/**
 * The preview pane's recent builds, for the Build log panel.
 *
 * <p>This used to be how an agent run asked the open preview "does this still build?". A run now
 * owns its own bundler (`agent/runChecks.ts`, ADR 0033) — the preview only ever builds the site on
 * screen, and a run may be working on another one, or outlive the page — so what remains is the
 * pane's own history.</p>
 */

export interface BuildSnapshot {
  ok: boolean;
  problems: BuildProblem[];
  /** VFS revision the build was made from, so a stale result can be recognised as stale. */
  revision: number;
  at: number;
}

const MAX_HISTORY = 20;
let history: BuildSnapshot[] = [];
const historyListeners = new Set<() => void>();

export function buildHistory(): readonly BuildSnapshot[] {
  return history;
}

export function subscribeBuilds(listener: () => void): () => void {
  historyListeners.add(listener);
  return () => {
    historyListeners.delete(listener);
  };
}

export function publishBuildResult(ok: boolean, problems: BuildProblem[], revision: number): void {
  history = [...history, { ok, problems, revision, at: Date.now() }].slice(-MAX_HISTORY);
  for (const listener of historyListeners) listener();
}
