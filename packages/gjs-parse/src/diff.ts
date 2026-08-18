/**
 * Line diff statistics — the `+12 −3` beside each file in the Source Control
 * changes list.
 *
 * This module deliberately imports nothing. It is reachable as
 * `@dcms/gjs-parse/diff` so the diff worker can bundle it on its own, without
 * dragging in htmlparser2, css-tree and js-beautify — the Mode B IDE shows the
 * same changes list and has no reason to load a parser it never uses.
 *
 * Monaco's diff editor already computes the *rendered* diff in its own worker;
 * what it cannot give us is a count for a file nobody has opened, which is
 * exactly what the list needs.
 */

export interface DiffStat {
  added: number;
  removed: number;
  /**
   * True when the counts are a whole-file approximation rather than a real
   * diff — see `MAX_EDIT_DISTANCE`. Callers can show them without a `+/−`
   * breakdown they cannot stand behind.
   */
  approximate: boolean;
}

/**
 * Myers runs in O(ND): cheap for an edit, quadratic for two unrelated files.
 * Past this many edits the answer is "it was rewritten", and computing that
 * precisely helps nobody.
 */
const MAX_EDIT_DISTANCE = 4000;

export function diffStat(original: string | null, modified: string | null): DiffStat {
  if (original === null && modified === null) return { added: 0, removed: 0, approximate: false };
  if (original === null) return { added: lineCount(modified!), removed: 0, approximate: false };
  if (modified === null) return { added: 0, removed: lineCount(original), approximate: false };
  if (original === modified) return { added: 0, removed: 0, approximate: false };

  const a = splitLines(original);
  const b = splitLines(modified);

  // Trim the identical head and tail first. Real edits touch a small middle, so
  // this alone usually reduces the problem to a handful of lines.
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let tail = 0;
  while (
    tail < a.length - head &&
    tail < b.length - head &&
    a[a.length - 1 - tail] === b[b.length - 1 - tail]
  ) {
    tail++;
  }

  const left = a.slice(head, a.length - tail);
  const right = b.slice(head, b.length - tail);
  if (left.length === 0) return { added: right.length, removed: 0, approximate: false };
  if (right.length === 0) return { added: 0, removed: left.length, approximate: false };

  // Compare interned ids rather than strings: the inner loop of Myers is a long
  // run of equality checks, and integer comparison makes it materially faster.
  const ids = new Map<string, number>();
  const intern = (line: string) => {
    let id = ids.get(line);
    if (id === undefined) {
      id = ids.size;
      ids.set(line, id);
    }
    return id;
  };
  const x = left.map(intern);
  const y = right.map(intern);

  const distance = editDistance(x, y, MAX_EDIT_DISTANCE);
  if (distance === null) {
    return { added: right.length, removed: left.length, approximate: true };
  }

  // Myers' D counts insertions plus deletions, and the two sides differ by the
  // length difference, so the split follows from those two facts alone.
  const removed = (distance - (right.length - left.length)) / 2;
  return { added: distance - removed, removed, approximate: false };
}

/** Sum of stats over a set of changed files. */
export function totalStat(stats: readonly DiffStat[]): DiffStat {
  return stats.reduce<DiffStat>(
    (acc, s) => ({
      added: acc.added + s.added,
      removed: acc.removed + s.removed,
      approximate: acc.approximate || s.approximate,
    }),
    { added: 0, removed: 0, approximate: false },
  );
}

function splitLines(text: string): string[] {
  if (text === '') return [];
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  // A trailing newline terminates the last line rather than starting an empty
  // one; counting it would report a phantom `+1` on every file that ends well.
  if (lines[lines.length - 1] === '') lines.pop();
  return lines;
}

function lineCount(text: string): number {
  return splitLines(text).length;
}

/**
 * Myers' greedy shortest-edit-script length. Returns null once the script grows
 * past `maxD`, which is the caller's signal to fall back to whole-file counts.
 */
function editDistance(a: readonly number[], b: readonly number[], maxD: number): number | null {
  const n = a.length;
  const m = b.length;
  const limit = Math.min(n + m, maxD);
  const offset = limit + 1;
  // Furthest-reaching x on each diagonal k, for the current D.
  const v = new Int32Array(2 * limit + 3);
  v[offset + 1] = 0;

  for (let d = 0; d <= limit; d++) {
    for (let k = -d; k <= d; k += 2) {
      const index = offset + k;
      // Extend downward when the diagonal below reached further, otherwise
      // rightward — the greedy step that makes this O(ND) rather than O(NM).
      let x =
        k === -d || (k !== d && v[index - 1]! < v[index + 1]!) ? v[index + 1]! : v[index - 1]! + 1;
      let y = x - k;
      while (x < n && y < m && a[x] === b[y]) {
        x++;
        y++;
      }
      v[index] = x;
      if (x >= n && y >= m) return d;
    }
  }
  return null;
}
