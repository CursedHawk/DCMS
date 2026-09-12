import type { ToolSpec } from '../../agent/contracts';
import { summarize } from '../../agent/projectIndex';
import type { AgentTransaction } from '../../agent/transaction';
import {
  formatHits,
  formatTree,
  type AgentWorkspace,
  type SearchMode,
} from '../../agent/workspace';
import type { ProjectIndex } from '../../agent/projectIndex';

/**
 * The Mode B coding tools, over the `AgentWorkspace` rather than the store.
 *
 * <p>This replaces a five-tool surface — list every path, read whole files, overwrite whole
 * files — whose shape was most of why runs were expensive. The model could not find anything
 * without reading everything, and could not change anything without rewriting it.</p>
 *
 * <p>Every description here is prompt engineering, not documentation: it is what the model reads
 * to decide which tool to reach for, so each one says when to prefer it over the obvious
 * alternative. The cheap tools advertise themselves; the expensive ones point at the cheap
 * ones.</p>
 */

export interface IdeToolContext {
  workspace: AgentWorkspace;
  tx: AgentTransaction;
  index: ProjectIndex;
}

type IdeTool = ToolSpec<IdeToolContext>;

const str = (input: Record<string, unknown>, key: string): string =>
  typeof input[key] === 'string' ? (input[key] as string) : '';

const num = (input: Record<string, unknown>, key: string): number | undefined =>
  typeof input[key] === 'number' ? (input[key] as number) : undefined;

export const IDE_TOOLS: IdeTool[] = [
  {
    name: 'project_overview',
    description:
      'The project at a glance: file count, entry point, dependencies, routes and components. Call this FIRST on an unfamiliar site — it is one cheap call that usually removes the need to list or read anything.',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
    run: async (_input, ctx) => ({ content: summarize(ctx.index) }),
  },

  {
    name: 'list_files',
    description:
      'List one directory level: subdirectories with their file counts, and the files directly inside. Pass `path` to descend. Prefer project_overview or search when you are looking for something specific rather than browsing.',
    input_schema: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Directory prefix, e.g. "src".' } },
      additionalProperties: false,
    },
    describe: (input) => `list ${str(input, 'path') || '/'}`,
    run: async (input, ctx) => {
      const prefix = str(input, 'path');
      return { content: formatTree(ctx.workspace.tree(prefix), prefix) };
    },
  },

  {
    name: 'search',
    description:
      'Find where something is, as compact path:line hits. mode="symbol" finds where a name is DEFINED (one or two hits, rather than the forty a text search for a component name returns). mode="references" finds where it is USED. mode="text" (default) matches literally anywhere. This is almost always cheaper than reading files to look for something.',
    input_schema: {
      type: 'object',
      properties: {
        query: { type: 'string' },
        mode: { type: 'string', enum: ['text', 'symbol', 'references'] },
        paths: {
          type: 'array',
          items: { type: 'string' },
          description: 'Restrict to these path prefixes.',
        },
        maxResults: { type: 'number' },
        regex: { type: 'boolean', description: 'Treat query as a regular expression (text mode).' },
        caseSensitive: { type: 'boolean' },
      },
      required: ['query'],
      additionalProperties: false,
    },
    describe: (input) => `search "${str(input, 'query')}"`,
    run: async (input, ctx) => {
      const response = ctx.workspace.search(str(input, 'query'), {
        mode: (input.mode as SearchMode) ?? 'text',
        paths: Array.isArray(input.paths) ? (input.paths as string[]) : undefined,
        maxResults: num(input, 'maxResults'),
        regex: input.regex === true,
        caseSensitive: input.caseSensitive === true,
      });
      return { content: formatHits(response), isError: Boolean(response.error) };
    },
  },

  {
    name: 'read_file',
    description:
      'Read a file, or part of one. Pass startLine/endLine to read a range — do this when you already know roughly where to look, because a whole large file is mostly tokens you will not use. Pass ifHash with a hash you already hold and the file comes back as "unchanged" for almost nothing, instead of being sent again.',
    input_schema: {
      type: 'object',
      properties: {
        path: { type: 'string' },
        startLine: { type: 'number', description: '1-based, inclusive.' },
        endLine: { type: 'number', description: '1-based, inclusive.' },
        ifHash: { type: 'string', description: 'Skip the read if the file still hashes to this.' },
      },
      required: ['path'],
      additionalProperties: false,
    },
    describe: (input) => `read ${str(input, 'path')}`,
    run: async (input, ctx) => {
      const result = ctx.workspace.read(str(input, 'path'), {
        startLine: num(input, 'startLine'),
        endLine: num(input, 'endLine'),
        ifHash: str(input, 'ifHash') || undefined,
      });

      if ('error' in result) return { content: result.error, isError: true };
      if ('unchanged' in result) {
        return { content: `${result.path} is unchanged (hash ${result.hash}).` };
      }

      const where = result.range
        ? `${result.path} lines ${result.range.start}-${result.range.end} of ${result.totalLines}`
        : `${result.path} (${result.totalLines} lines)`;
      // The hash rides along on every read so the model can guard its next edit with it
      // without a second call to ask what it is.
      return { content: `${where}, hash ${result.hash}\n\n${result.content}` };
    },
  },

  {
    name: 'edit_file',
    description:
      'Replace one exact, unique run of text. THE DEFAULT WAY TO CHANGE CODE — far cheaper than rewriting a file, and safer. old_text must appear exactly once; include surrounding lines to make it unique. Pass expected_hash (from read_file) so the edit is refused rather than silently overwriting a change made while you were thinking.',
    input_schema: {
      type: 'object',
      properties: {
        path: { type: 'string' },
        old_text: { type: 'string' },
        new_text: { type: 'string' },
        expected_hash: { type: 'string' },
        replace_all: {
          type: 'boolean',
          description: 'Replace every occurrence. Use only for a deliberate rename.',
        },
      },
      required: ['path', 'old_text', 'new_text'],
      additionalProperties: false,
    },
    risk: 'safe',
    describe: (input) => `edit ${str(input, 'path')}`,
    summarize: (input) => `Edit ${str(input, 'path')}`,
    run: async (input, ctx) =>
      ctx.tx.patch(str(input, 'path'), {
        oldText: str(input, 'old_text'),
        newText: str(input, 'new_text'),
        expectedHash: str(input, 'expected_hash') || undefined,
        replaceAll: input.replace_all === true,
      }),
  },

  {
    name: 'replace_lines',
    description:
      'Replace an inclusive 1-based line range. Use when the text to replace is awkward to anchor exactly — a whole function body, a block with repeated punctuation. Line numbers must come from a read in this same turn; they are refused if they fall outside the file.',
    input_schema: {
      type: 'object',
      properties: {
        path: { type: 'string' },
        start_line: { type: 'number' },
        end_line: { type: 'number' },
        text: { type: 'string' },
        expected_hash: { type: 'string' },
      },
      required: ['path', 'start_line', 'end_line', 'text'],
      additionalProperties: false,
    },
    risk: 'safe',
    describe: (input) => `edit ${str(input, 'path')}`,
    summarize: (input) =>
      `Replace lines ${num(input, 'start_line')}-${num(input, 'end_line')} of ${str(input, 'path')}`,
    run: async (input, ctx) =>
      ctx.tx.replaceRange(str(input, 'path'), {
        startLine: num(input, 'start_line') ?? 0,
        endLine: num(input, 'end_line') ?? 0,
        text: str(input, 'text'),
        expectedHash: str(input, 'expected_hash') || undefined,
      }),
  },

  {
    name: 'insert_lines',
    description:
      'Insert text before a 1-based line. Pass line 1 to prepend (an import, a header) or lineCount+1 to append.',
    input_schema: {
      type: 'object',
      properties: {
        path: { type: 'string' },
        line: { type: 'number' },
        text: { type: 'string' },
        expected_hash: { type: 'string' },
      },
      required: ['path', 'line', 'text'],
      additionalProperties: false,
    },
    risk: 'safe',
    describe: (input) => `edit ${str(input, 'path')}`,
    summarize: (input) => `Insert into ${str(input, 'path')} at line ${num(input, 'line')}`,
    run: async (input, ctx) =>
      ctx.tx.insertAt(str(input, 'path'), {
        line: num(input, 'line') ?? 1,
        text: str(input, 'text'),
        expectedHash: str(input, 'expected_hash') || undefined,
      }),
  },

  {
    name: 'delete_lines',
    description:
      'Delete an inclusive 1-based line range. Line numbers must come from a read in this same turn; they are refused if they fall outside the file. Prefer edit_file when the text to remove is easy to anchor exactly — an anchored delete cannot silently take the wrong lines after something above it shifted.',
    input_schema: {
      type: 'object',
      properties: {
        path: { type: 'string' },
        start_line: { type: 'number' },
        end_line: { type: 'number' },
        expected_hash: { type: 'string' },
      },
      required: ['path', 'start_line', 'end_line'],
      additionalProperties: false,
    },
    risk: 'safe',
    describe: (input) => `edit ${str(input, 'path')}`,
    summarize: (input) =>
      `Delete lines ${num(input, 'start_line')}-${num(input, 'end_line')} of ${str(input, 'path')}`,
    run: async (input, ctx) =>
      ctx.tx.deleteRange(str(input, 'path'), {
        startLine: num(input, 'start_line') ?? 0,
        endLine: num(input, 'end_line') ?? 0,
        expectedHash: str(input, 'expected_hash') || undefined,
      }),
  },

  {
    name: 'create_file',
    description:
      'Create a NEW file with full contents. Refused if the path already exists — use edit_file to change an existing file.',
    input_schema: {
      type: 'object',
      properties: { path: { type: 'string' }, content: { type: 'string' } },
      required: ['path', 'content'],
      additionalProperties: false,
    },
    risk: 'safe',
    describe: (input) => `create ${str(input, 'path')}`,
    summarize: (input) => `Create ${str(input, 'path')}`,
    run: async (input, ctx) => ctx.tx.create(str(input, 'path'), str(input, 'content')),
  },

  {
    name: 'delete_file',
    description:
      'Delete a file from the site. Use sparingly: an import left pointing at it breaks the build, so check with search first that nothing references it. Emptying a file with edit_file is usually what is wanted instead, and is easier for the author to undo.',
    input_schema: {
      type: 'object',
      properties: { path: { type: 'string' } },
      required: ['path'],
      additionalProperties: false,
    },
    // Dangerous, not safe: unlike an edit, there is no earlier version of a deleted file in the
    // working draft to go back to until the run's change set is reverted as a whole.
    risk: 'dangerous',
    describe: (input) => `delete ${str(input, 'path')}`,
    summarize: (input) => `Delete ${str(input, 'path')}`,
    run: async (input, ctx) => ctx.tx.remove(str(input, 'path')),
  },

  {
    name: 'rename_file',
    description: 'Move or rename a file. Refused if the destination already exists.',
    input_schema: {
      type: 'object',
      properties: { from: { type: 'string' }, to: { type: 'string' } },
      required: ['from', 'to'],
      additionalProperties: false,
    },
    risk: 'safe',
    describe: (input) => `rename ${str(input, 'from')}`,
    summarize: (input) => `Rename ${str(input, 'from')} to ${str(input, 'to')}`,
    run: async (input, ctx) => ctx.tx.rename(str(input, 'from'), str(input, 'to')),
  },
];
