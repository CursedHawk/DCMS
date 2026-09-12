/**
 * The edit primitives, as pure functions over text.
 *
 * <p>Nothing here touches a store. An edit is "given this content, produce that content, or
 * explain why not" — which makes every rule in it testable without a VFS, and keeps the one
 * place that mutates the workspace (`transaction.ts`) small enough to reason about.</p>
 *
 * <h3>Why failures are values</h3>
 * <p>Every one of these returns a result rather than throwing. An agent edit fails <i>routinely</i>
 * and recoverably — the anchor moved, the human typed into the file, the line numbers came from a
 * stale read — and each of those has a different repair. A thrown error flattens them into one
 * `catch` that can only say "it didn't work"; a typed reason lets the runtime re-anchor a patch
 * automatically (see `rebase`) and lets the model be told exactly what to do differently.</p>
 */

export type EditFailureReason =
  | 'not-found'
  | 'hash-mismatch'
  | 'anchor-missing'
  | 'anchor-ambiguous'
  | 'out-of-range'
  | 'unchanged'
  | 'exists'
  | 'binary'
  | 'invalid-path';

export interface EditFailure {
  ok: false;
  reason: EditFailureReason;
  /** Written for the model: what went wrong and what to do instead. */
  message: string;
  /** Present on `hash-mismatch`, so the caller can re-read at the right version. */
  currentHash?: string;
}

export interface EditSuccess {
  ok: true;
  content: string;
  /** 1-based line range the edit touched, for the change-review pane. */
  touched: { start: number; end: number };
}

export type EditResult = EditSuccess | EditFailure;

const fail = (reason: EditFailureReason, message: string, currentHash?: string): EditFailure => ({
  ok: false,
  reason,
  message,
  ...(currentHash ? { currentHash } : {}),
});

/**
 * Replace one exact, unique run of text.
 *
 * <p>The workhorse, and the reason it insists on uniqueness: an agent that replaces "the first
 * occurrence" will eventually replace the wrong one in a file with repeated structure, and the
 * result compiles. Demanding a unique anchor turns that silent corruption into a refusal the
 * model answers by including more surrounding context — which it is good at.</p>
 *
 * <p>`replaceAll` is the deliberate escape hatch for a genuine rename, where every occurrence is
 * the intent rather than an ambiguity.</p>
 */
export function applyAnchoredPatch(
  content: string,
  oldText: string,
  newText: string,
  options: { replaceAll?: boolean } = {},
): EditResult {
  if (oldText === '') {
    return fail('anchor-missing', 'old_text must not be empty. To create a file, use create.');
  }
  if (oldText === newText) {
    return fail('unchanged', 'old_text and new_text are identical — this edit would do nothing.');
  }

  const first = content.indexOf(oldText);
  if (first === -1) {
    return fail(
      'anchor-missing',
      'old_text was not found. Re-read the file — it may have changed, or the whitespace may differ.',
    );
  }

  if (!options.replaceAll) {
    const second = content.indexOf(oldText, first + oldText.length);
    if (second !== -1) {
      return fail(
        'anchor-ambiguous',
        `old_text appears at least twice (offsets ${first} and ${second}). Include more surrounding context to identify one, or pass replace_all.`,
      );
    }
  }

  const updated = options.replaceAll
    ? content.split(oldText).join(newText)
    : content.slice(0, first) + newText + content.slice(first + oldText.length);

  const startLine = lineAt(content, first);
  return {
    ok: true,
    content: updated,
    touched: { start: startLine, end: startLine + countLines(newText) - 1 },
  };
}

/**
 * Replace an inclusive, 1-based line range.
 *
 * <p>Unlike a read, a write range is <b>not</b> clamped. A read that clamps returns nearby code
 * and costs nothing; a write that clamps deletes lines the caller never named. When the numbers
 * are wrong here, the only safe answer is to refuse and say what the file's real extent is.</p>
 */
export function applyReplaceRange(
  content: string,
  startLine: number,
  endLine: number,
  newText: string,
): EditResult {
  const lines = content.split('\n');
  const check = checkRange(startLine, endLine, lines.length);
  if (check) return check;

  const replacement = newText === '' ? [] : newText.split('\n');
  lines.splice(startLine - 1, endLine - startLine + 1, ...replacement);
  return {
    ok: true,
    content: lines.join('\n'),
    touched: { start: startLine, end: startLine + Math.max(replacement.length, 1) - 1 },
  };
}

/** Delete an inclusive, 1-based line range. */
export function applyDeleteRange(content: string, startLine: number, endLine: number): EditResult {
  return applyReplaceRange(content, startLine, endLine, '');
}

/**
 * Insert text before a given 1-based line.
 *
 * <p>`afterLine` semantics were rejected deliberately: "insert at line 1" is unambiguous where
 * "insert after line 0" is a puzzle, and prepending to a file is a common operation (an import,
 * a licence header). Passing `lines.length + 1` appends.</p>
 */
export function applyInsertAt(content: string, line: number, text: string): EditResult {
  const lines = content.split('\n');
  if (!Number.isInteger(line) || line < 1 || line > lines.length + 1) {
    return fail(
      'out-of-range',
      `line must be between 1 and ${lines.length + 1} (the file has ${lines.length} lines).`,
    );
  }
  const inserted = text.split('\n');
  lines.splice(line - 1, 0, ...inserted);
  return {
    ok: true,
    content: lines.join('\n'),
    touched: { start: line, end: line + inserted.length - 1 },
  };
}

function checkRange(start: number, end: number, total: number): EditFailure | null {
  if (!Number.isInteger(start) || !Number.isInteger(end)) {
    return fail('out-of-range', 'Line numbers must be integers.');
  }
  if (start < 1 || start > total) {
    return fail(
      'out-of-range',
      `startLine ${start} is outside the file, which has ${total} lines.`,
    );
  }
  if (end < start) {
    return fail('out-of-range', `endLine ${end} is before startLine ${start}.`);
  }
  if (end > total) {
    return fail('out-of-range', `endLine ${end} is outside the file, which has ${total} lines.`);
  }
  return null;
}

/** 1-based line number containing a character offset. */
function lineAt(content: string, offset: number): number {
  let line = 1;
  for (let i = 0; i < offset; i++) if (content.charCodeAt(i) === 10) line++;
  return line;
}

function countLines(text: string): number {
  let n = 1;
  for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) n++;
  return n;
}

/**
 * Try to re-anchor a patch whose file moved under it.
 *
 * <p>The common case by far is that the human edited a <i>different part</i> of the same file
 * while the agent was thinking. The anchor is still there, still unique, and the patch is still
 * exactly right — it is only the hash that changed. Refusing that and making the model re-read
 * and re-reason costs a full turn to arrive at the same edit.</p>
 *
 * <p>So a mismatch is retried once against the current content, and succeeds only if the anchor
 * is still unique. If the anchor is gone or has become ambiguous, the human's edit genuinely
 * overlaps the agent's and re-anchoring would be guessing — that is when the model has to look
 * again.</p>
 */
export function rebaseAnchoredPatch(
  currentContent: string,
  oldText: string,
  newText: string,
): EditResult {
  return applyAnchoredPatch(currentContent, oldText, newText);
}
