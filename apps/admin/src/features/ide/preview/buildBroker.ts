import type { BuildProblem } from './problems';

/**
 * Lets the agent ask the live preview "does this still build?" without starting a second bundler.
 *
 * <p>The esbuild worker is owned by `usePreview`, which is a React hook attached to the preview
 * pane. The agent is not a component and has no access to it — and standing up its own worker
 * would mean two WASM instances bundling the same project on every keystroke, which is exactly
 * the waste the existing single-worker comment warns about.</p>
 *
 * <p>So the pane publishes into this broker and the agent reads from it. The pane stays the only
 * thing that owns a worker; the broker is a notice board.</p>
 *
 * <h3>It degrades honestly</h3>
 * <p>When the preview pane is closed there is no worker, and the correct answer is "I cannot
 * check" — not a hang, and not a cheerful "no problems" derived from a build that never ran. A
 * validation tool that reports success when it did not run is worse than no validation tool,
 * because the model will trust it.</p>
 */

export interface BuildSnapshot {
  ok: boolean;
  problems: BuildProblem[];
  /** VFS revision the build was made from, so a stale result can be recognised as stale. */
  revision: number;
  at: number;
}

let latest: BuildSnapshot | null = null;
let refresher: (() => void) | null = null;
let waiters: ((snapshot: BuildSnapshot) => void)[] = [];

/**
 * Recent builds, newest last — what the Build tab shows.
 *
 * <p>The broker kept only the latest result, which answers "is it building now" and nothing
 * else. The question an author actually has after a failure is "did this just start, or has it
 * been broken since I touched that file?", and that needs more than one data point.</p>
 *
 * <p>Twenty is about an hour of ordinary editing. The list holds results, not logs: the
 * problems themselves are already in each snapshot.</p>
 */
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

/**
 * Called by the preview pane when it mounts. Returns the unregister function.
 *
 * <p>Registering also clears any stale snapshot: a pane that has just mounted has not built
 * anything yet, and the previous pane's last result may describe a different site.</p>
 */
export function registerPreview(refresh: () => void): () => void {
  refresher = refresh;
  latest = null;
  return () => {
    if (refresher === refresh) refresher = null;
    // Anyone waiting is told the truth rather than left hanging until their timeout.
    const pending = waiters;
    waiters = [];
    for (const resolve of pending) {
      resolve({ ok: false, problems: [], revision: -1, at: Date.now() });
    }
  };
}

/** Called by the preview pane after every build. */
export function publishBuildResult(ok: boolean, problems: BuildProblem[], revision: number): void {
  history = [...history, { ok, problems, revision, at: Date.now() }].slice(-MAX_HISTORY);
  for (const listener of historyListeners) listener();

  latest = { ok, problems, revision, at: Date.now() };
  const pending = waiters;
  waiters = [];
  for (const resolve of pending) resolve(latest);
}

/** True when a preview pane is attached and can actually build. */
export function previewAvailable(): boolean {
  return refresher !== null;
}

/** The most recent build, or null if none has completed since the pane mounted. */
export function latestBuild(): BuildSnapshot | null {
  return latest;
}

/**
 * Trigger a build and wait for its result.
 *
 * <p>Returns the cached snapshot when it already reflects the current revision — the common case
 * after the agent has just read files without changing them, and re-bundling to learn what is
 * already known is seconds of WASM for nothing.</p>
 */
export async function requestBuild(
  revision: number,
  timeoutMs = 30_000,
): Promise<BuildSnapshot | null> {
  if (!refresher) return null;
  if (latest && latest.revision === revision) return latest;

  const result = new Promise<BuildSnapshot>((resolve) => waiters.push(resolve));
  refresher();

  // A worker that dies mid-build would otherwise leave the run waiting forever; a timeout is
  // reported as "could not check", never as "no problems".
  const timeout = new Promise<null>((resolve) => setTimeout(() => resolve(null), timeoutMs));
  return Promise.race([result, timeout]);
}

/** Test seam: drop all broker state between cases. */
export function resetBroker(): void {
  history = [];
  historyListeners.clear();

  latest = null;
  refresher = null;
  waiters = [];
}
