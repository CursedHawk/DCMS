/**
 * This browser tab's identity, for telling its own draft saves apart from everyone else's.
 *
 * <p>A working draft is per account and per branch, so every `DraftChanged` message a tab
 * receives is about <i>its own</i> draft — written either by a second tab or by the AI agent.
 * The editor used to distinguish its own writes by version number: anything at or below the
 * version it already held was an echo.</p>
 *
 * <p>That works only while saves are strictly ordered, and they are not. An agent run flushing a
 * batch while the author keeps typing produces overlapping saves, and the version test then fails
 * in both directions — suppressing a genuine remote change as an echo, or announcing the tab's
 * own work back to it as "your draft changed elsewhere". The second is the false positive the
 * rework is meant to remove, and the first is worse: it is silent.</p>
 *
 * <p>An id is the identity that the version number was standing in for. It lives in
 * `sessionStorage`, not `localStorage`, because the unit is the <b>tab</b>: two tabs on the same
 * site must get different ids, and `localStorage` is shared across them. It survives a reload of
 * the same tab, which is what makes a save in flight across a refresh still recognisable.</p>
 */

const KEY = 'dcms.ide.clientId';

let cached: string | null = null;

export function clientId(): string {
  if (cached) return cached;
  try {
    const existing = sessionStorage.getItem(KEY);
    if (existing) {
      cached = existing;
      return existing;
    }
  } catch {
    // Private mode, or storage disabled. Fall through to a memory-only id: it is still unique
    // per tab for as long as the tab lives, which is all this is for.
  }

  const generated = crypto.randomUUID();
  cached = generated;
  try {
    sessionStorage.setItem(KEY, generated);
  } catch {
    // As above — an id that does not survive a reload is a smaller problem than no id.
  }
  return generated;
}
