import type { FieldType } from '../plugins/api';

/** How a rich field's value is stored. Decided by the field type, not by the editor. */
export type RichFormat = 'html' | 'markdown';

/**
 * Which serialisation a field expects.
 *
 * <p>`RichText` is markup: a site template binds it with the `html` target precisely because it
 * is the one target that trusts the value. `Markdown` is source text and stays source text —
 * storing HTML in it would break every consumer that reads it as markdown, and there is no
 * migration that could tell the two apart afterwards.</p>
 */
export function formatOf(type: FieldType): RichFormat {
  return type === 'Markdown' ? 'markdown' : 'html';
}

/**
 * An editor that has been emptied must store nothing, not the markup of an empty document.
 *
 * <p>ProseMirror always holds at least one paragraph, so clearing the editor serialises to
 * `<p></p>` — a non-empty string. Every required-field check on this platform tests the stored
 * value, so without this a required body could be satisfied by deleting its contents, and a
 * "has the author written anything yet" check would answer yes for every item ever opened.</p>
 */
export function normalizeRich(value: string): string {
  // Order matters: the entity and the break have to go before the empty-paragraph test, or
  // `<p>&nbsp;</p>` -- what a browser leaves behind after select-all-delete in some engines --
  // survives as content.
  const stripped = value
    .replace(/&nbsp;/gi, ' ')
    .replace(/<br\s*\/?>/gi, '')
    .replace(/<p>\s*<\/p>/gi, '')
    .trim();
  return stripped.length === 0 ? '' : value;
}

/**
 * Whether an incoming value should replace what the editor currently holds.
 *
 * <p>TipTap owns its own document, so re-applying a value on every render would reset the
 * selection and make the caret jump to the start on every keystroke. The editor is therefore
 * only reloaded when the incoming value differs from what it last emitted — which is exactly
 * the case where the change came from somewhere else: a different item was opened, a draft was
 * reloaded, or a collaborator's edit arrived.</p>
 */
export function shouldAdopt(incoming: string, lastEmitted: string | null): boolean {
  return lastEmitted === null ? incoming.length > 0 : incoming !== lastEmitted;
}
