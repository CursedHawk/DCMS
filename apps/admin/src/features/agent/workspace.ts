import { isBinaryPath } from '../site-source/binary';
import type { FileSlice, SearchHit, UnchangedFile, WorkspaceRevision } from './contracts';
import { cachedHash } from './hash';
import { findDefinitions, findImporters, type ProjectIndex } from './projectIndex';

/**
 * The agent's view of the site, and the only door it gets.
 *
 * <p>Two rules shape everything here. The first is that <b>the agent has no special powers</b>:
 * this reads the same in-memory VFS the editor renders and writes through the same guarded path,
 * so a human and an agent are two clients of one workspace rather than two sources of truth. The
 * second is that <b>the model is never handed the project</b>. It gets a tree summary, search
 * hits, and slices of files it asked for by line — never a dump of everything on the chance that
 * some of it is relevant.</p>
 *
 * <p>It is deliberately a plain object over a snapshot, not a hook. A run pins one
 * {@link WorkspaceRevision} and reads against it; tools are async and the store moves underneath
 * them, so "the files as they were when this call started" has to be a value, not a subscription.</p>
 */
export interface AgentWorkspace {
  readonly revision: WorkspaceRevision;

  /** Every path, sorted. The raw list — prefer {@link tree} for anything the model reads. */
  paths(): string[];

  /**
   * A directory summary rather than a path dump.
   *
   * <p>`list_files` on a real project spends hundreds of tokens telling the model about
   * directories it will never open. A tree one level at a time lets it navigate for a fraction
   * of that, and the counts tell it where the code actually is.</p>
   */
  tree(prefix?: string): TreeEntry[];

  /** The current content hash, or null when the path does not exist. */
  hashOf(path: string): string | null;

  /**
   * Read a file, or a slice of one.
   *
   * <p>Returns {@link UnchangedFile} when `ifHash` matches what is already there — the answer to
   * the read/edit/re-read loop that otherwise eats a run's budget.</p>
   */
  read(path: string, options?: ReadOptions): FileSlice | UnchangedFile | ReadError;

  /** Compact `path:line` hits. Deliberately small: a place to look, not the thing itself. */
  search(query: string, options?: SearchOptions): SearchResponse;
}

export interface TreeEntry {
  /** Path relative to the requested prefix. */
  name: string;
  kind: 'file' | 'dir';
  /** For a directory, how many files sit under it at any depth. */
  files?: number;
  /** For a file, its size in characters — enough to decide whether to read it whole. */
  size?: number;
}

export interface ReadOptions {
  /** 1-based, inclusive. Omit both for the whole file (subject to {@link MAX_WHOLE_FILE_LINES}). */
  startLine?: number;
  endLine?: number;
  /** Skip the read if the file still hashes to this. */
  ifHash?: string;
}

export interface ReadError {
  path: string;
  error: string;
}

/**
 * How to interpret the query.
 *
 * <p><b>text</b> — literal (or regex) match anywhere. The fallback, and what to use when the
 * model is looking for a string rather than a name.</p>
 * <p><b>symbol</b> — where this name is <i>defined</i>. One or two hits instead of the forty a
 * text search for a common component name returns, nearly all of which are usages.</p>
 * <p><b>references</b> — where it is <i>used</i>: the files importing it, plus textual mentions,
 * minus the definition itself.</p>
 */
export type SearchMode = 'text' | 'symbol' | 'references';

export interface SearchOptions {
  mode?: SearchMode;
  /** Restrict to paths starting with any of these prefixes. */
  paths?: readonly string[];
  maxResults?: number;
  /** Treat the query as a regular expression rather than literal text. Text mode only. */
  regex?: boolean;
  caseSensitive?: boolean;
}

export interface SearchResponse {
  hits: SearchHit[];
  /** True when the cap was hit and the answer is a prefix of the truth. */
  truncated: boolean;
  /** How many files were examined, so "no hits" can be told from "nothing searched". */
  filesSearched: number;
  /** Set when a regex query will not compile. */
  error?: string;
}

/**
 * The point past which a whole-file read is refused and a range is required.
 *
 * <p>Not a performance limit — it is in memory — but a budget one. A 2,000-line file is most of
 * a small context window, and an agent that can pull one in without meaning to will. Past this
 * the read returns the head plus an explicit instruction to ask for a range, which teaches the
 * narrower request instead of silently truncating.</p>
 */
export const MAX_WHOLE_FILE_LINES = 400;

/** How many hits a search returns before it says it stopped. */
export const DEFAULT_MAX_HITS = 40;

/** How much of a matching line is worth showing. Enough to judge relevance, not to read code. */
const PREVIEW_CHARS = 160;

/**
 * Build a workspace over a file map.
 *
 * <p>Takes the map rather than reaching into the store, so a run can pin a snapshot and so this
 * is testable without a zustand store standing behind it.</p>
 */
export function createWorkspace(
  files: Readonly<Record<string, string>>,
  meta: { siteId: string; branch: string; revision: number },
  index?: ProjectIndex,
): AgentWorkspace {
  /**
   * Search results for this revision.
   *
   * <p>An agent re-runs the same search more often than it looks like it does — after a failed
   * patch, after a validation error naming a symbol it already looked up. The revision is in the
   * key, so a cached answer can never survive an edit; this workspace is a snapshot, so the
   * cache lives and dies with it.</p>
   */
  const searchCache = new Map<string, SearchResponse>();
  // Hashes are computed lazily per path and memoised on content, so building a workspace for
  // every tool call costs nothing until something is actually read.
  const hashes: Record<string, string> = {};
  const hashOf = (path: string): string | null => {
    const content = files[path];
    if (content === undefined) return null;
    return (hashes[path] ??= cachedHash(content));
  };

  const revision: WorkspaceRevision = {
    siteId: meta.siteId,
    branch: meta.branch,
    revision: meta.revision,
    // A getter-backed proxy would be neater, but the contract says this is a plain readonly map
    // and a run may want to compare two revisions. Filled on demand by `hashOf` above; callers
    // that need every hash ask for them.
    get hashes() {
      for (const path of Object.keys(files)) hashOf(path);
      return hashes;
    },
  };

  return {
    revision,

    paths: () => Object.keys(files).sort(),

    hashOf,

    tree(prefix = '') {
      const base = prefix ? `${prefix.replace(/\/+$/, '')}/` : '';
      const dirs = new Map<string, number>();
      const out: TreeEntry[] = [];

      for (const path of Object.keys(files)) {
        if (base && !path.startsWith(base)) continue;
        const rest = path.slice(base.length);
        const slash = rest.indexOf('/');
        if (slash === -1) {
          out.push({ name: rest, kind: 'file', size: files[path].length });
        } else {
          const dir = rest.slice(0, slash);
          dirs.set(dir, (dirs.get(dir) ?? 0) + 1);
        }
      }

      for (const [name, count] of dirs) out.push({ name, kind: 'dir', files: count });
      // Directories first, then files, each alphabetical — the order every file tree uses, and
      // the one that makes a listing scannable rather than a jumble.
      return out.sort((a, b) =>
        a.kind === b.kind ? a.name.localeCompare(b.name) : a.kind === 'dir' ? -1 : 1,
      );
    },

    read(path, options = {}) {
      const content = files[path];
      if (content === undefined) return { path, error: `File not found: ${path}` };
      if (isBinaryPath(path)) {
        // Held as base64 in the same map. Returning it would spend thousands of tokens on
        // something the model cannot read anyway.
        return { path, error: `${path} is a binary file and cannot be read as text.` };
      }

      const hash = hashOf(path)!;
      if (options.ifHash && options.ifHash === hash) return { path, hash, unchanged: true };

      const lines = content.split('\n');
      const totalLines = lines.length;
      const hasRange = options.startLine !== undefined || options.endLine !== undefined;

      if (!hasRange && totalLines > MAX_WHOLE_FILE_LINES) {
        const head = lines.slice(0, MAX_WHOLE_FILE_LINES).join('\n');
        return {
          path,
          hash,
          totalLines,
          range: { start: 1, end: MAX_WHOLE_FILE_LINES },
          content: `${head}\n\n… ${totalLines - MAX_WHOLE_FILE_LINES} more lines. Re-read with startLine/endLine for the rest.`,
        };
      }

      if (!hasRange) return { path, hash, content, totalLines };

      // Clamped rather than rejected: an off-by-one or a stale line number from an earlier read
      // should return the nearby code, not an error the model has to recover from.
      const start = Math.max(1, Math.min(options.startLine ?? 1, totalLines));
      const end = Math.max(start, Math.min(options.endLine ?? totalLines, totalLines));
      return {
        path,
        hash,
        totalLines,
        range: { start, end },
        content: lines.slice(start - 1, end).join('\n'),
      };
    },

    search(query, options = {}) {
      const max = options.maxResults ?? DEFAULT_MAX_HITS;
      if (!query) return { hits: [], truncated: false, filesSearched: 0 };

      const mode = options.mode ?? 'text';
      const key = `${mode} ${query} ${options.paths?.join(',') ?? ''} ${max} ${options.regex ? 1 : 0}${options.caseSensitive ? 1 : 0}`;
      const cached = searchCache.get(key);
      if (cached) return cached;

      const result = runSearch(query, mode, max, options);
      searchCache.set(key, result);
      return result;
    },
  };

  /**
   * Symbol and reference modes lean on the index; without one they fall back to text rather than
   * refusing. A degraded answer the model can still act on beats an error it has to work around.
   */
  function runSearch(
    query: string,
    mode: SearchMode,
    max: number,
    options: SearchOptions,
  ): SearchResponse {
    if (mode === 'symbol' && index) {
      const hits = findDefinitions(index, query)
        .filter((d) => !options.paths?.length || options.paths.some((p) => d.path.startsWith(p)))
        .slice(0, max)
        .map((d) => ({
          path: d.path,
          line: d.line,
          preview: `${d.exported ? 'export ' : ''}${d.kind} ${d.name}`,
        }));
      // A name the scanner never saw is not proof it does not exist — the scanner is a regex,
      // not a parser — so fall through to text rather than reporting nothing.
      if (hits.length > 0) return { hits, truncated: false, filesSearched: hits.length };
    }

    if (mode === 'references' && index) {
      const definedIn = new Set(findDefinitions(index, query).map((d) => `${d.path}:${d.line}`));
      const importers = new Set(findImporters(index, query));
      const textual = textSearch(query, max, options);
      const hits = textual.hits.filter((h) => !definedIn.has(`${h.path}:${h.line}`));
      // Importers that matched nothing textually still belong: `import { Hero } from …` on a
      // line the filter removed, or a re-export.
      for (const path of importers) {
        if (hits.length >= max) break;
        if (!hits.some((h) => h.path === path)) {
          hits.push({ path, line: 1, preview: `(imports ${query})` });
        }
      }
      return { hits, truncated: textual.truncated, filesSearched: textual.filesSearched };
    }

    return textSearch(query, max, options);
  }

  function textSearch(query: string, max: number, options: SearchOptions): SearchResponse {
    let pattern: RegExp;
    try {
      const source = options.regex ? query : escapeRegExp(query);
      pattern = new RegExp(source, options.caseSensitive ? 'g' : 'gi');
    } catch (err) {
      return { hits: [], truncated: false, filesSearched: 0, error: (err as Error).message };
    }

    const hits: SearchHit[] = [];
    let filesSearched = 0;

    for (const path of Object.keys(files).sort()) {
      // Binary files are base64 in this map; a few hex-ish characters would otherwise match
      // the middle of a PNG and offer it as a place to look.
      if (isBinaryPath(path)) continue;
      if (options.paths?.length && !options.paths.some((p) => path.startsWith(p))) continue;

      filesSearched++;
      const lines = files[path].split('\n');
      for (let i = 0; i < lines.length; i++) {
        pattern.lastIndex = 0;
        if (!pattern.test(lines[i])) continue;

        // One hit per line, not per match. The model is deciding where to look; being told the
        // same line matched four times costs four times as much and says the same thing.
        hits.push({ path, line: i + 1, preview: clip(lines[i]) });
        if (hits.length >= max) return { hits, truncated: true, filesSearched };
      }
    }

    return { hits, truncated: false, filesSearched };
  }
}

function clip(line: string): string {
  const trimmed = line.trim();
  return trimmed.length <= PREVIEW_CHARS ? trimmed : `${trimmed.slice(0, PREVIEW_CHARS)}…`;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Render a tree listing as the compact text a tool result carries.
 *
 * <p>Formatting belongs here rather than in the tool, because the shape is the thing being
 * economised: `src/ (42 files)` is one line the model can act on, where the same information as
 * JSON is four.</p>
 */
export function formatTree(entries: readonly TreeEntry[], prefix = ''): string {
  if (entries.length === 0) return prefix ? `(nothing under ${prefix})` : '(empty site)';
  return entries
    .map((e) => (e.kind === 'dir' ? `${e.name}/ (${e.files} files)` : e.name))
    .join('\n');
}

/** Render search hits as `path:line  preview`, aligned enough to scan. */
export function formatHits(response: SearchResponse): string {
  if (response.error) return `Invalid pattern: ${response.error}`;
  if (response.hits.length === 0) {
    return `No matches in ${response.filesSearched} files.`;
  }
  const body = response.hits.map((h) => `${h.path}:${h.line}  ${h.preview}`).join('\n');
  return response.truncated
    ? `${body}\n\n(stopped at ${response.hits.length} matches — narrow the query or pass paths)`
    : body;
}
