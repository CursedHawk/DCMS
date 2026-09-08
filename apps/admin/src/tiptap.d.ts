import type { MarkdownStorage } from 'tiptap-markdown';

/**
 * `tiptap-markdown` ships its storage type but does not register it, so
 * `editor.storage.markdown` is a type error even though it is the documented way to read the
 * serialised value. `@tiptap/core` exports `Storage` as an empty interface for exactly this —
 * declaring it here is what the extension would do itself if it targeted TypeScript users.
 *
 * <p>Without it the alternative is a cast at every call site, which is the same assertion made
 * repeatedly and unchecked.</p>
 */
declare module '@tiptap/core' {
  interface Storage {
    markdown: MarkdownStorage;
  }
}
