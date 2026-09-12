/**
 * Conflict-marker text, the way git writes it.
 *
 * <p>The IDE's answer to a conflict used to be "keep mine" or "keep theirs" for the whole file,
 * which is only ever right by luck: two people editing different parts of the same file both
 * did work worth keeping, and picking a side throws one of them away silently. Offering the
 * marked-up text lets the reader merge by hand, in the same shape they would see in any other
 * git client.</p>
 */

export type ResolutionKind = 'mine' | 'theirs' | 'manual';

export type Resolution =
  { kind: 'mine' } | { kind: 'theirs' } | { kind: 'manual'; content: string };

export interface ConflictFile {
  path: string;
  /** The version on the branch being merged in, or in the tab you are typing in. */
  mine: string | null;
  /** The version already on the target branch, or the one the server now holds. */
  theirs: string | null;
  /** The common ancestor, when the producer knows it. */
  base?: string | null;
}

export const MARKER_MINE = '<<<<<<<';
export const MARKER_SPLIT = '=======';
export const MARKER_THEIRS = '>>>>>>>';

/** A line that opens, splits or closes a conflict block. */
const MARKER_LINE = /^(<{7}|={7}|>{7})(\s|$)/;

/**
 * The starting text for a hand merge: both sides, marked up.
 *
 * A null side means the file is absent there — an add/add or a delete/modify conflict — and the
 * block is written with an empty side rather than being skipped, because "one side deleted this"
 * is exactly the case a reader needs to see rather than infer.
 */
export function buildConflictText(
  file: ConflictFile,
  mineLabel = 'mine',
  theirsLabel = 'theirs',
): string {
  const mine = file.mine ?? '';
  const theirs = file.theirs ?? '';
  if (mine === theirs) return mine;
  return [
    `${MARKER_MINE} ${mineLabel}`,
    mine,
    MARKER_SPLIT,
    theirs,
    `${MARKER_THEIRS} ${theirsLabel}`,
  ].join('\n');
}

/**
 * Whether any conflict marker is still in the text.
 *
 * The guard on "this file is resolved". Committing a file with markers still in it produces
 * source that does not parse and a build that fails minutes later, by which point the merge is
 * already on the branch.
 */
export function hasUnresolvedMarkers(text: string): boolean {
  return text.split('\n').some((line) => MARKER_LINE.test(line));
}

/** The 1-based line numbers of every marker, for pointing the editor at the first one. */
export function markerLines(text: string): number[] {
  return text
    .split('\n')
    .map((line, index) => (MARKER_LINE.test(line) ? index + 1 : 0))
    .filter((n) => n > 0);
}

/** The content a resolution commits. `null` deletes the file, which the API understands. */
export function resolvedContent(file: ConflictFile, resolution: Resolution): string | null {
  switch (resolution.kind) {
    case 'mine':
      return file.mine;
    case 'theirs':
      return file.theirs;
    case 'manual':
      return resolution.content;
  }
}

/**
 * Whether every file has an answer that can actually be committed.
 *
 * A manual resolution still holding markers does not count, and neither does one whose file
 * has no entry at all — the merge button stays disabled rather than committing a half-answer.
 */
export function isFullyResolved(
  files: readonly ConflictFile[],
  resolutions: Readonly<Record<string, Resolution>>,
): boolean {
  return files.every((file) => {
    const resolution = resolutions[file.path];
    if (!resolution) return false;
    if (resolution.kind !== 'manual') return true;
    return !hasUnresolvedMarkers(resolution.content);
  });
}

/** How many files are settled, for the "3 of 7 resolved" counter. */
export function resolvedCount(
  files: readonly ConflictFile[],
  resolutions: Readonly<Record<string, Resolution>>,
): number {
  return files.filter((file) => isFullyResolved([file], resolutions)).length;
}
