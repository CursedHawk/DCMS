import type { ToolCall, ToolSpec } from '../../agent/contracts';

/**
 * What an approval card should show, derived from the call the model actually made.
 *
 * <h3>Why a diff and not the tool call</h3>
 * <p>The card used to read <c>edit_file src/App.tsx</c> with a JSON blob under it. That asks
 * somebody to approve a change by reading its <i>arguments</i> — and since the arguments are
 * always plausible, the honest answer is always yes. A person approving a change to their site
 * needs to see the change to their site.</p>
 *
 * <p>So each call is turned into lines that came out and lines that went in, and the card renders
 * them the way every other diff in the IDE is rendered. Tools that do not touch files — publish,
 * unpublish, delete media — have no diff to show and fall back to their own `summarize()`, which
 * the registry already requires of every gated tool.</p>
 *
 * <p>Pure, and separately tested, because the alternative is proving diff derivation through a
 * rendered component.</p>
 */

export type ChangePreview =
  | { kind: 'edit'; path: string; removed: string[]; added: string[]; atLine?: number }
  | { kind: 'create'; path: string; added: string[] }
  | { kind: 'delete'; path: string; removed: string[] }
  /** Not a file change: publishing, scheduling, deleting media. One sentence, future tense. */
  | { kind: 'action'; text: string };

/** How many lines of one change the card shows before it says how many more there are. */
export const PREVIEW_LINES = 12;

function lines(text: string): string[] {
  // A trailing newline is a property of the file, not a line in it; showing it as an empty
  // added line makes every append look like it inserted a blank.
  const out = text.split('\n');
  if (out.length > 1 && out[out.length - 1] === '') out.pop();
  return out;
}

function str(input: Record<string, unknown>, key: string): string {
  const value = input[key];
  return typeof value === 'string' ? value : '';
}

function num(input: Record<string, unknown>, key: string): number | undefined {
  const value = input[key];
  return typeof value === 'number' ? value : undefined;
}

/** The 1-based line `old_text` starts on, so the card can say where in the file this is. */
function lineOf(content: string | undefined, needle: string): number | undefined {
  if (!content || !needle) return undefined;
  const at = content.indexOf(needle);
  if (at === -1) return undefined;
  return content.slice(0, at).split('\n').length;
}

/**
 * Turn one gated call into something a person can judge.
 *
 * <p>`files` is the workspace as it stands <b>before</b> the call runs — which is the only state
 * in which a preview means anything, and the reason this is computed at the card rather than
 * after the fact.</p>
 */
export function previewOf(
  call: ToolCall,
  files: Readonly<Record<string, string>>,
  spec?: Pick<ToolSpec<unknown>, 'summarize'>,
): ChangePreview {
  const input = call.input ?? {};
  const path = str(input, 'path');
  const current = files[path];

  switch (call.name) {
    case 'edit_file':
      return {
        kind: 'edit',
        path,
        removed: lines(str(input, 'old_text')),
        added: lines(str(input, 'new_text')),
        atLine: lineOf(current, str(input, 'old_text')),
      };

    case 'replace_lines': {
      const start = num(input, 'start_line');
      const end = num(input, 'end_line');
      const existing = current ? current.split('\n') : [];
      return {
        kind: 'edit',
        path,
        removed: start && end ? existing.slice(start - 1, end) : [],
        added: lines(str(input, 'text')),
        atLine: start,
      };
    }

    case 'insert_lines':
      // `line` is the line inserted *before*, so it is already where the new text lands.
      return {
        kind: 'edit',
        path,
        removed: [],
        added: lines(str(input, 'text')),
        atLine: num(input, 'line'),
      };

    case 'delete_lines': {
      const start = num(input, 'start_line');
      const end = num(input, 'end_line');
      const existing = current ? current.split('\n') : [];
      return {
        kind: 'edit',
        path,
        removed: start && end ? existing.slice(start - 1, end) : [],
        added: [],
        atLine: start,
      };
    }

    case 'create_file':
      return { kind: 'create', path, added: lines(str(input, 'content')) };

    case 'delete_file':
      // The whole file, because that is what is being agreed to. Clipped for display like any
      // other change — the count is what carries the weight here.
      return { kind: 'delete', path, removed: current ? lines(current) : [] };

    case 'rename_file':
      // `from`/`to`, not `path` — the one workspace tool that does not take a `path`.
      return { kind: 'action', text: `Rename ${str(input, 'from')} to ${str(input, 'to')}` };

    default:
      return { kind: 'action', text: spec?.summarize?.(input) ?? `Run ${call.name}` };
  }
}

/** A one-line heading for a preview: what happens, and to what. */
export function previewTitle(preview: ChangePreview): string {
  switch (preview.kind) {
    case 'edit':
      return preview.atLine ? `${preview.path}:${preview.atLine}` : preview.path;
    case 'create':
      return preview.path;
    case 'delete':
      return preview.path;
    case 'action':
      return preview.text;
  }
}

/**
 * How many lines a batch adds and removes in total.
 *
 * <p>The number a person actually weighs before deciding — "two files, +14 −3" is a judgement
 * they can make in a second, where four collapsed diffs is one they have to reconstruct.</p>
 */
export function countLines(previews: readonly ChangePreview[]): { added: number; removed: number } {
  let added = 0;
  let removed = 0;
  for (const preview of previews) {
    if (preview.kind === 'edit') {
      added += preview.added.length;
      removed += preview.removed.length;
    } else if (preview.kind === 'create') {
      added += preview.added.length;
    } else if (preview.kind === 'delete') {
      removed += preview.removed.length;
    }
  }
  return { added, removed };
}
