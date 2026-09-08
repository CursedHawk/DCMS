import type { GitChange } from './git';

/**
 * What discarding one file's changes means, decided before anything is touched.
 *
 * <p>Separated from the click handler because the three cases are not symmetrical and the
 * asymmetry is the whole content of the feature: undoing an <em>added</em> file is a delete,
 * undoing a <em>deleted</em> one is a write, and the middle case is the only one that looks
 * like what the button says.</p>
 */
export type DiscardPlan =
  | { kind: 'write'; path: string; content: string }
  | { kind: 'delete'; path: string }
  | { kind: 'unavailable'; path: string };

/**
 * <p><b>A truncated change cannot be discarded.</b> The server elides the content of very
 * large or binary files, so `headContent` is null for a file that certainly has one — writing
 * that back would replace the file with nothing while reporting success, which is the worst
 * available outcome for a button whose entire job is to restore something. Refusing and saying
 * why is the only honest answer.</p>
 */
export function discardPlan(change: GitChange): DiscardPlan {
  if (change.status === 'added') {
    // Never existed at HEAD, so undoing it means removing it. `truncated` is irrelevant here:
    // there is no content to restore.
    return { kind: 'delete', path: change.path };
  }

  if (change.truncated || change.headContent === null) {
    return { kind: 'unavailable', path: change.path };
  }

  // Both 'modified' and 'deleted' restore the committed content; the store treats a write to a
  // deleted path as an undelete, so one branch serves both.
  return { kind: 'write', path: change.path, content: change.headContent };
}
