/**
 * The matching behind the IDE's command palette.
 *
 * <p>Kept apart from the dialog because it is the only part with a right answer. Whether
 * `apcx` finds `src/api/client.tsx`, and whether it ranks above `src/app/components/x.ts`, is
 * the difference between a palette people reach for and one they open once — and it is exactly
 * the kind of thing that is tedious to check by hand and trivial to check with a test.</p>
 */

/** A candidate that matched, with the character positions that did the matching. */
export interface Ranked<T> {
  item: T;
  score: number;
  /** Indices into the searched string, for highlighting. Ascending. */
  positions: number[];
}

const SEPARATORS = new Set(['/', '-', '_', '.', ' ']);

/**
 * Subsequence match, scored.
 *
 * <p>Every query character must appear in order; what varies is how well. The scoring is the
 * short list of things that make a file finder feel right:</p>
 *
 * <ul>
 *   <li><b>Consecutive characters</b> beat scattered ones, so `client` prefers `client.ts` over
 *   a path that happens to contain those letters spread across three segments.</li>
 *   <li><b>Segment starts</b> — after a `/`, `-`, `_`, `.`, or at a camelCase hump — score
 *   highest, which is what makes initials work: `acx` finds `api/client.tsx`.</li>
 *   <li><b>Later is worse</b>, mildly, so a match near the front of the name wins a tie.</li>
 * </ul>
 *
 * <p>Greedy, not optimal: it takes the first occurrence of each query character rather than
 * searching every alignment. An optimal matcher is a dynamic program over path length × query
 * length, run for every file on every keystroke, to change the order of results that are all
 * already plausible. Returns null when the candidate does not contain the query at all.</p>
 */
export function fuzzyScore(
  candidate: string,
  query: string,
): { score: number; positions: number[] } | null {
  if (query === '') return { score: 0, positions: [] };

  const haystack = candidate.toLowerCase();
  const needle = query.toLowerCase();
  const positions: number[] = [];

  let score = 0;
  let at = 0;

  for (const char of needle) {
    const found = haystack.indexOf(char, at);
    if (found === -1) return null;

    const previous = positions.length > 0 ? positions[positions.length - 1] : -2;
    if (found === previous + 1) score += 10;

    const before = found === 0 ? '/' : candidate[found - 1];
    const isBoundary =
      found === 0 ||
      SEPARATORS.has(before) ||
      (before === before.toLowerCase() && candidate[found] !== candidate[found].toLowerCase());
    if (isBoundary) score += 12;

    score -= Math.min(found, 20) * 0.1;
    positions.push(found);
    at = found + 1;
  }

  return { score, positions };
}

/**
 * Ranks file paths against a query.
 *
 * <p>Matched against the <b>basename first</b> and the full path second. People search for a
 * file by its name; matching the whole path with equal weight means typing `app` surfaces every
 * file under `src/app/` before `App.tsx` itself, which is the wrong answer to the question
 * being asked. A path-only match still appears, below the name matches, because narrowing by
 * folder is the other half of what a file finder is for.</p>
 */
export function rankPaths(paths: readonly string[], query: string, limit = 50): Ranked<string>[] {
  const trimmed = query.trim();

  if (trimmed === '') {
    return [...paths]
      .sort((a, b) => a.localeCompare(b))
      .slice(0, limit)
      .map((item) => ({ item, score: 0, positions: [] }));
  }

  const ranked: Ranked<string>[] = [];
  for (const path of paths) {
    const slash = path.lastIndexOf('/');
    const nameAt = slash + 1;

    const byName = fuzzyScore(path.slice(nameAt), trimmed);
    if (byName) {
      ranked.push({
        item: path,
        // A name match outranks any path match, so the two never interleave.
        score: byName.score + 1000,
        positions: byName.positions.map((p) => p + nameAt),
      });
      continue;
    }

    const byPath = fuzzyScore(path, trimmed);
    if (byPath) ranked.push({ item: path, score: byPath.score, positions: byPath.positions });
  }

  return ranked.sort((a, b) => b.score - a.score || a.item.localeCompare(b.item)).slice(0, limit);
}

/** One thing the palette can do. */
export interface IdeCommand {
  id: string;
  label: string;
  /** The shortcut, already formatted for this platform. Shown, not parsed. */
  shortcut?: string;
  /** Extra words that should find this command — "scm" for Source control. */
  keywords?: string;
  run: () => void;
  /** Rendered dimmed and unselectable. Used for what is unavailable right now, with a reason. */
  disabled?: boolean;
}

/**
 * Ranks commands against a query, over the label and its keywords together.
 *
 * <p>Keywords exist because the name a person reaches for is often not the name on the button:
 * somebody who wants the Source Control view types `git`, and somebody who wants Problems types
 * `errors`. Matching the label alone makes the palette a memory test.</p>
 */
export function rankCommands(commands: readonly IdeCommand[], query: string): Ranked<IdeCommand>[] {
  const trimmed = query.trim();
  if (trimmed === '') return commands.map((item) => ({ item, score: 0, positions: [] }));

  const ranked: Ranked<IdeCommand>[] = [];
  for (const command of commands) {
    const byLabel = fuzzyScore(command.label, trimmed);
    if (byLabel) {
      ranked.push({ item: command, score: byLabel.score + 1000, positions: byLabel.positions });
      continue;
    }
    // Keyword hits are found but not highlighted: the positions are indices into a string the
    // reader is not being shown, and painting them onto the label would highlight the wrong
    // characters.
    if (command.keywords && fuzzyScore(command.keywords, trimmed)) {
      ranked.push({ item: command, score: 0, positions: [] });
    }
  }

  return ranked.sort((a, b) => b.score - a.score || a.item.label.localeCompare(b.item.label));
}

/**
 * Splits a string into matched and unmatched runs, for highlighting.
 *
 * <p>Runs rather than one span per character: a six-character match is one `<mark>`, not six
 * adjacent ones, which matters for both the DOM and how it reads when a screen reader announces
 * emphasis.</p>
 */
export function highlightRuns(
  text: string,
  positions: readonly number[],
): { text: string; hit: boolean }[] {
  if (positions.length === 0) return [{ text, hit: false }];

  const hits = new Set(positions);
  const runs: { text: string; hit: boolean }[] = [];

  for (let i = 0; i < text.length; i++) {
    const hit = hits.has(i);
    const last = runs[runs.length - 1];
    if (last && last.hit === hit) last.text += text[i];
    else runs.push({ text: text[i], hit });
  }

  return runs;
}

/**
 * `⌘` on a Mac, `Ctrl` everywhere else.
 *
 * <p>Shown, never parsed — the handlers check `metaKey || ctrlKey` and accept either, because a
 * Mac keyboard plugged into Linux is a real thing and refusing one of them is a bug nobody can
 * diagnose. This only decides what the shortcut sheet prints.</p>
 */
export function modifierLabel(platform: string = navigator.platform): string {
  return /mac|iphone|ipad/i.test(platform) ? '⌘' : 'Ctrl';
}
