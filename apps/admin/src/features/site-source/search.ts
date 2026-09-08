import { isBinaryPath } from './binary';

export interface SearchOptions {
  caseSensitive: boolean;
  wholeWord: boolean;
  useRegex: boolean;
}

export interface SearchMatch {
  path: string;
  /** 1-based, so it can be handed straight to the editor. */
  line: number;
  /** 1-based column of the match within the line. */
  column: number;
  /** The whole line, for context in the results list. */
  lineText: string;
  /** Where the match sits inside `lineText`, for highlighting. */
  start: number;
  end: number;
}

export interface SearchResult {
  matches: SearchMatch[];
  /** True when the cap was reached and the answer is a prefix of the truth. */
  truncated: boolean;
  /** Set when the query is a regular expression that will not compile. */
  error?: string;
}

/**
 * How many matches are worth listing.
 *
 * <p>Not a performance limit — searching a site's few hundred in-memory files is microseconds —
 * but a usefulness one. A results panel with four thousand rows in it is not a search, and the
 * honest response to "your query matches everything" is to say so rather than to render it.</p>
 */
export const MAX_MATCHES = 500;

/**
 * Project-wide search over the working draft.
 *
 * <p>Entirely in memory, because the draft already is: the whole point of the VFS is that the
 * editor has every file. That also makes this search what the author is <em>looking at</em>
 * rather than what was last committed, which is the only version they can act on.</p>
 *
 * <p><b>Binary files are skipped</b>, not searched and found empty. They are held as base64 in
 * the same map, so a query of a few hex-ish characters would otherwise match the middle of an
 * image and offer to open it in a text editor.</p>
 */
export function searchFiles(
  files: Record<string, string>,
  query: string,
  options: SearchOptions,
): SearchResult {
  if (query.length === 0) {
    return { matches: [], truncated: false };
  }

  let pattern: RegExp;
  try {
    pattern = buildPattern(query, options);
  } catch (err) {
    // A half-typed regular expression is the normal state of the box while somebody is typing
    // one. Saying so beats an empty result that looks like "no matches".
    return { matches: [], truncated: false, error: (err as Error).message };
  }

  const matches: SearchMatch[] = [];

  for (const path of Object.keys(files).sort()) {
    if (isBinaryPath(path)) continue;

    const lines = files[path].split('\n');
    for (let i = 0; i < lines.length; i++) {
      const lineText = lines[i];
      // Reset between lines: a /g/ regex carries lastIndex across calls, which would make
      // every other line skip its first match.
      pattern.lastIndex = 0;

      let m: RegExpExecArray | null;
      while ((m = pattern.exec(lineText)) !== null) {
        matches.push({
          path,
          line: i + 1,
          column: m.index + 1,
          lineText,
          start: m.index,
          end: m.index + m[0].length,
        });

        if (matches.length >= MAX_MATCHES) {
          return { matches, truncated: true };
        }

        // A pattern that can match the empty string ( `a*`, `\b` ) would otherwise never
        // advance and loop forever on one line.
        if (m[0].length === 0) pattern.lastIndex++;
      }
    }
  }

  return { matches, truncated: false };
}

/** Matches grouped by file, in the order the files were searched. */
export function groupByFile(matches: readonly SearchMatch[]): { path: string; matches: SearchMatch[] }[] {
  const groups: { path: string; matches: SearchMatch[] }[] = [];
  for (const match of matches) {
    const last = groups[groups.length - 1];
    if (last?.path === match.path) last.matches.push(match);
    else groups.push({ path: match.path, matches: [match] });
  }
  return groups;
}

function buildPattern(query: string, options: SearchOptions): RegExp {
  const source = options.useRegex ? query : escapeRegExp(query);
  // \b around a pattern the user wrote is deliberate and occasionally surprising, but it is
  // what every editor's "whole word" does and matching that expectation matters more than
  // being clever about which side of an alternation the boundary lands on.
  const withWord = options.wholeWord ? `\\b(?:${source})\\b` : source;
  return new RegExp(withWord, options.caseSensitive ? 'g' : 'gi');
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
