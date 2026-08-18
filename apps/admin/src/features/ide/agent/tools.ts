import { isToolchainFile, normalizePath, useVfs } from '../../site-source';
import type { ToolUseBlock } from './client';

// The agent's file tools, executed locally against the live VFS store so edits
// land in open tabs and the preview refreshes in real time. Mutations are confined
// to safe, non-toolchain paths (the site-builder rejects toolchain files anyway).

export interface ToolResult {
  content: string;
  isError?: boolean;
}

/** True for tools that change files — gated behind approval in manual mode. */
export function isMutation(name: string): boolean {
  return name === 'write_file' || name === 'edit_file' || name === 'delete_file';
}

export const AGENT_TOOLS = [
  {
    name: 'list_files',
    description: 'List every file path in the current site (the working draft).',
    input_schema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'read_file',
    description: 'Read the full contents of one file by its path.',
    input_schema: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Repo-relative file path, e.g. src/App.tsx' } },
      required: ['path'],
      additionalProperties: false,
    },
  },
  {
    name: 'write_file',
    description:
      'Create a new file or overwrite an existing one with the given full contents. Use for new files or whole-file rewrites; prefer edit_file for small changes.',
    input_schema: {
      type: 'object',
      properties: {
        path: { type: 'string' },
        content: { type: 'string', description: 'The complete new file contents.' },
      },
      required: ['path', 'content'],
      additionalProperties: false,
    },
  },
  {
    name: 'edit_file',
    description:
      'Replace exactly one occurrence of old_string with new_string in a file. old_string must match uniquely (include surrounding context to disambiguate).',
    input_schema: {
      type: 'object',
      properties: {
        path: { type: 'string' },
        old_string: { type: 'string' },
        new_string: { type: 'string' },
      },
      required: ['path', 'old_string', 'new_string'],
      additionalProperties: false,
    },
  },
  {
    name: 'delete_file',
    description: 'Delete a file from the site.',
    input_schema: {
      type: 'object',
      properties: { path: { type: 'string' } },
      required: ['path'],
      additionalProperties: false,
    },
  },
];

export function runTool(block: ToolUseBlock): ToolResult {
  const input = block.input ?? {};
  try {
    switch (block.name) {
      case 'list_files':
        return ok(Object.keys(useVfs.getState().files).sort().join('\n') || '(empty site)');
      case 'read_file':
        return readFile(String(input.path ?? ''));
      case 'write_file':
        return writeFile(String(input.path ?? ''), String(input.content ?? ''));
      case 'edit_file':
        return editFile(String(input.path ?? ''), String(input.old_string ?? ''), String(input.new_string ?? ''));
      case 'delete_file':
        return deleteFile(String(input.path ?? ''));
      default:
        return err(`Unknown tool: ${block.name}`);
    }
  } catch (e) {
    return err(e instanceof Error ? e.message : 'Tool execution failed.');
  }
}

function readFile(rawPath: string): ToolResult {
  const path = normalizePath(rawPath);
  if (!path) return err(`Invalid path: ${rawPath}`);
  const content = useVfs.getState().files[path];
  if (content === undefined) return err(`File not found: ${path}`);
  return ok(content);
}

function writeFile(rawPath: string, content: string): ToolResult {
  const path = normalizePath(rawPath);
  if (!path) return err(`Invalid path: ${rawPath}`);
  if (isToolchainFile(path)) return err(`${path} is a platform-managed toolchain file and cannot be edited.`);
  const vfs = useVfs.getState();
  const existed = vfs.files[path] !== undefined;
  vfs.writeFile(path, content);
  vfs.open(path);
  return ok(existed ? `Overwrote ${path}.` : `Created ${path}.`);
}

function editFile(rawPath: string, oldStr: string, newStr: string): ToolResult {
  const path = normalizePath(rawPath);
  if (!path) return err(`Invalid path: ${rawPath}`);
  if (isToolchainFile(path)) return err(`${path} is a platform-managed toolchain file and cannot be edited.`);
  const vfs = useVfs.getState();
  const content = vfs.files[path];
  if (content === undefined) return err(`File not found: ${path}`);
  if (oldStr === '') return err('old_string must not be empty.');
  const first = content.indexOf(oldStr);
  if (first === -1) return err(`old_string not found in ${path}.`);
  if (content.indexOf(oldStr, first + oldStr.length) !== -1) {
    return err(`old_string is not unique in ${path}; add more surrounding context.`);
  }
  vfs.writeFile(path, content.slice(0, first) + newStr + content.slice(first + oldStr.length));
  vfs.open(path);
  return ok(`Edited ${path}.`);
}

function deleteFile(rawPath: string): ToolResult {
  const path = normalizePath(rawPath);
  if (!path) return err(`Invalid path: ${rawPath}`);
  if (isToolchainFile(path)) return err(`${path} is a platform-managed toolchain file and cannot be deleted.`);
  const vfs = useVfs.getState();
  if (vfs.files[path] === undefined) return err(`File not found: ${path}`);
  vfs.deleteFile(path);
  return ok(`Deleted ${path}.`);
}

const ok = (content: string): ToolResult => ({ content });
const err = (content: string): ToolResult => ({ content, isError: true });
