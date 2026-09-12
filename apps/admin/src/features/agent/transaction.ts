import type { ToolOutcome } from './contracts';
import {
  applyAnchoredPatch,
  applyDeleteRange,
  applyInsertAt,
  applyReplaceRange,
  type EditResult,
} from './edits';
import { cachedHash } from './hash';

/**
 * The write side of the workspace: one run's edits, applied as they happen and reviewable as a
 * set.
 *
 * <h3>What "transaction" means here, and what it does not</h3>
 * <p>Edits land in the workspace <b>immediately</b> — the author watches files change in their
 * open tabs and the preview rebuild, which is most of what makes the agent feel like a
 * collaborator rather than a batch job. So this is not a transaction in the sense of isolation;
 * nothing is hidden until commit.</p>
 *
 * <p>What it batches is the <i>server save</i> and the <i>review</i>. Without it a five-file
 * change is five draft deltas, five `DraftChanged` messages and five entries in the author's
 * history; with it there is one save, one notification, and one change set to accept or revert.
 * The hold on autosave is what makes that true even when a run takes longer than the debounce.</p>
 */

/** The store operations a transaction needs. Abstract so the rules are testable without a VFS. */
export interface WorkspacePort {
  read(path: string): string | undefined;
  write(path: string, content: string): void;
  remove(path: string): void;
  exists(path: string): boolean;
  /** Normalise and validate a path the way the backend will. Null means it must be rejected. */
  normalize(path: string): string | null;
}

export type ChangeKind = 'created' | 'modified' | 'deleted';

export interface FileChange {
  path: string;
  kind: ChangeKind;
  /** Content before the run touched it. Null for a file the run created — the revert is a delete. */
  before: string | null;
  /** Content now. Null for a deleted file. */
  after: string | null;
}

export interface AgentTransaction {
  patch(
    path: string,
    args: { oldText: string; newText: string; expectedHash?: string; replaceAll?: boolean },
  ): ToolOutcome;
  replaceRange(
    path: string,
    args: { startLine: number; endLine: number; text: string; expectedHash?: string },
  ): ToolOutcome;
  insertAt(path: string, args: { line: number; text: string; expectedHash?: string }): ToolOutcome;
  deleteRange(
    path: string,
    args: { startLine: number; endLine: number; expectedHash?: string },
  ): ToolOutcome;
  create(path: string, content: string): ToolOutcome;
  remove(path: string): ToolOutcome;
  rename(from: string, to: string): ToolOutcome;

  /** Every file this run touched, in the order first touched. */
  changes(): FileChange[];
  /** Undo the whole set, restoring each file to what it was before the run. */
  revertAll(): void;
  /** Undo one file. */
  revert(path: string): boolean;
}

export interface TransactionOptions {
  /** Notified per applied edit, so the UI can stream `file.changed` events. */
  onChange?: (change: FileChange) => void;
}

export function createTransaction(
  port: WorkspacePort,
  options: TransactionOptions = {},
): AgentTransaction {
  /**
   * Original content per path, captured the first time the run touches it.
   *
   * <p>Keyed by path and never overwritten, so revert restores the state before the <i>run</i>,
   * not before the last edit. An agent that patches the same file four times should be revertable
   * in one move, not four.</p>
   */
  const before = new Map<string, string | null>();
  const order: string[] = [];

  function note(path: string): void {
    if (before.has(path)) return;
    before.set(path, port.exists(path) ? (port.read(path) ?? '') : null);
    order.push(path);
  }

  function emit(path: string): void {
    const original = before.get(path) ?? null;
    const current = port.exists(path) ? (port.read(path) ?? '') : null;
    const kind: ChangeKind =
      original === null ? 'created' : current === null ? 'deleted' : 'modified';
    options.onChange?.({ path, kind, before: original, after: current });
  }

  /** Shared preamble: resolve the path, confirm it exists, and enforce the hash guard. */
  type Opened = { ok: true; content: string; path: string } | { ok: false; outcome: ToolOutcome };

  function open(path: string, expectedHash: string | undefined): Opened {
    const resolved = port.normalize(path);
    if (!resolved) return { ok: false, outcome: err(`Invalid path: ${path}`) };
    const content = port.read(resolved);
    if (content === undefined) return { ok: false, outcome: err(`File not found: ${resolved}`) };

    if (expectedHash) {
      const actual = cachedHash(content);
      if (actual !== expectedHash) {
        // The whole point of the guard. Telling the model the current hash means its retry can
        // be a re-read at the right version rather than a guess.
        return {
          ok: false,
          outcome: err(
            `${resolved} changed since you read it (expected ${expectedHash}, now ${actual}). Re-read it and redo this edit.`,
          ),
        };
      }
    }
    return { ok: true, content, path: resolved };
  }

  function commit(path: string, result: EditResult, verb: string): ToolOutcome {
    if (!result.ok) return err(result.message);
    note(path);
    port.write(path, result.content);
    emit(path);
    return {
      content: `${verb} ${path} (lines ${result.touched.start}–${result.touched.end}). New hash: ${cachedHash(result.content)}`,
      paths: [path],
    };
  }

  return {
    patch(path, args) {
      const o = open(path, args.expectedHash);
      if (!o.ok) return o.outcome;
      return commit(
        o.path,
        applyAnchoredPatch(o.content, args.oldText, args.newText, { replaceAll: args.replaceAll }),
        'Patched',
      );
    },

    replaceRange(path, args) {
      const o = open(path, args.expectedHash);
      if (!o.ok) return o.outcome;
      return commit(
        o.path,
        applyReplaceRange(o.content, args.startLine, args.endLine, args.text),
        'Edited',
      );
    },

    insertAt(path, args) {
      const o = open(path, args.expectedHash);
      if (!o.ok) return o.outcome;
      return commit(o.path, applyInsertAt(o.content, args.line, args.text), 'Inserted into');
    },

    deleteRange(path, args) {
      const o = open(path, args.expectedHash);
      if (!o.ok) return o.outcome;
      return commit(
        o.path,
        applyDeleteRange(o.content, args.startLine, args.endLine),
        'Deleted from',
      );
    },

    create(path, content) {
      const resolved = port.normalize(path);
      if (!resolved) return err(`Invalid path: ${path}`);
      if (port.exists(resolved)) {
        // Not an overwrite. A create that silently replaces an existing file is how a run
        // destroys work nobody asked it to touch.
        return err(`${resolved} already exists. Use patch to change it, or delete it first.`);
      }
      note(resolved);
      port.write(resolved, content);
      emit(resolved);
      return { content: `Created ${resolved} (${countLines(content)} lines).`, paths: [resolved] };
    },

    remove(path) {
      const resolved = port.normalize(path);
      if (!resolved) return err(`Invalid path: ${path}`);
      if (!port.exists(resolved)) return err(`File not found: ${resolved}`);
      note(resolved);
      port.remove(resolved);
      emit(resolved);
      return { content: `Deleted ${resolved}.`, paths: [resolved] };
    },

    rename(from, to) {
      const src = port.normalize(from);
      const dst = port.normalize(to);
      if (!src) return err(`Invalid path: ${from}`);
      if (!dst) return err(`Invalid path: ${to}`);
      if (!port.exists(src)) return err(`File not found: ${src}`);
      if (port.exists(dst)) return err(`${dst} already exists.`);

      const content = port.read(src) ?? '';
      note(src);
      note(dst);
      port.remove(src);
      port.write(dst, content);
      emit(src);
      emit(dst);
      return { content: `Renamed ${src} to ${dst}.`, paths: [src, dst] };
    },

    changes() {
      const out: FileChange[] = [];
      for (const path of order) {
        const original = before.get(path) ?? null;
        const current = port.exists(path) ? (port.read(path) ?? '') : null;
        // A file edited and then edited back is not a change, and listing it as one sends the
        // author to review a diff that is empty.
        if (original === current) continue;
        out.push({
          path,
          kind: original === null ? 'created' : current === null ? 'deleted' : 'modified',
          before: original,
          after: current,
        });
      }
      return out;
    },

    revert(path) {
      if (!before.has(path)) return false;
      const original = before.get(path) ?? null;
      if (original === null) port.remove(path);
      else port.write(path, original);
      emit(path);
      return true;
    },

    revertAll() {
      // Reverse order, so a rename (which touches two paths) undoes its write before its delete.
      for (const path of [...order].reverse()) {
        const original = before.get(path) ?? null;
        if (original === null) port.remove(path);
        else port.write(path, original);
        emit(path);
      }
    },
  };
}

const err = (message: string): ToolOutcome => ({ content: message, isError: true });

function countLines(text: string): number {
  let n = 1;
  for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) n++;
  return n;
}
