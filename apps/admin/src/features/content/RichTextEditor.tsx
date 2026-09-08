import { EditorContent, useEditor, type Editor } from '@tiptap/react';
import StarterKit from '@tiptap/starter-kit';
import { Markdown } from 'tiptap-markdown';
import {
  Bold,
  Code,
  Heading2,
  Heading3,
  Italic,
  List,
  ListOrdered,
  Quote,
  Redo2,
  Strikethrough,
  Undo2,
} from 'lucide-react';
import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { cn, InfoHint } from '@dcms/ui';
import { normalizeRich, shouldAdopt, type RichFormat } from './richText';

/**
 * The editor for RichText and Markdown fields, which were a bare `<textarea>` until now.
 *
 * <p><b>Loaded on demand.</b> The whole ProseMirror stack is a few hundred kilobytes and only a
 * content type with a rich field ever needs it, so `ContentFieldInput` reaches this through
 * `React.lazy` and the bundler keeps it in a chunk of its own.</p>
 *
 * <p><b>The format is the field's, not the editor's.</b> A Markdown field stores markdown and a
 * RichText field stores HTML — see {@link formatOf}. The same editing surface serves both;
 * only the serialiser differs, which is the whole reason for choosing an editor with one.</p>
 *
 * <p><b>Uncontrolled inside, controlled outside.</b> TipTap owns its document. Re-applying the
 * value on every render would reset the selection and send the caret to the start on every
 * keystroke, so an incoming value is adopted only when it differs from what this editor last
 * emitted — see {@link shouldAdopt}.</p>
 */
export function RichTextEditor({
  value,
  onChange,
  format,
  id,
}: {
  value: string;
  onChange: (value: string) => void;
  format: RichFormat;
  id?: string;
}) {
  const { t } = useTranslation();

  // What we last handed the caller. Compared against incoming values so an echo of our own
  // change is not treated as an edit from elsewhere.
  const emittedRef = useRef<string | null>(null);

  const editor = useEditor({
    extensions: [
      StarterKit,
      // Registered for both formats, because it is also the parser: pasting markdown into an
      // HTML field is a thing people do, and the extension turns it into real structure rather
      // than a paragraph full of asterisks.
      Markdown.configure({ html: format === 'html', breaks: true, transformPastedText: true }),
    ],
    content: value,
    editorProps: {
      attributes: {
        // The accessible name and the association with the field's label both live on the
        // contenteditable element itself; the wrapper is not what gets focus.
        id: id ?? '',
        role: 'textbox',
        'aria-multiline': 'true',
        class: 'min-h-32 px-3 py-2 outline-none',
      },
    },
    onUpdate: ({ editor: e }) => {
      const next = normalizeRich(
        format === 'markdown' ? e.storage.markdown.getMarkdown() : e.getHTML(),
      );
      emittedRef.current = next;
      onChange(next);
    },
  });

  useEffect(() => {
    if (!editor) return;
    if (!shouldAdopt(value, emittedRef.current)) return;

    emittedRef.current = value;
    // emitUpdate false: this is somebody else's change arriving, and echoing it back as an
    // edit would mark a clean draft dirty just for having been opened.
    editor.commands.setContent(value, { emitUpdate: false });
  }, [editor, value]);

  if (!editor) return null;

  return (
    <div className="rounded-md border border-input bg-background focus-within:ring-1 focus-within:ring-ring">
      <Toolbar editor={editor} format={format} />
      <EditorContent
        editor={editor}
        className={cn(
          'text-sm',
          // A small typographic scale for the editing surface. Hand-written rather than pulled
          // from a typography plugin: it is a dozen rules, and they have to match what the
          // field will look like on a site rather than a general-purpose article style.
          '[&_.ProseMirror]:min-h-32 [&_.ProseMirror]:outline-none',
          '[&_.ProseMirror>*+*]:mt-2',
          '[&_h2]:text-base [&_h2]:font-semibold [&_h3]:text-sm [&_h3]:font-semibold',
          '[&_ul]:list-disc [&_ol]:list-decimal [&_ul]:pl-5 [&_ol]:pl-5',
          '[&_blockquote]:border-l-2 [&_blockquote]:border-border [&_blockquote]:pl-3 [&_blockquote]:text-muted-foreground',
          '[&_code]:rounded [&_code]:bg-muted [&_code]:px-1 [&_code]:py-0.5 [&_code]:font-mono [&_code]:text-xs',
          '[&_pre]:overflow-x-auto [&_pre]:rounded [&_pre]:bg-muted [&_pre]:p-3 [&_pre]:text-xs',
          '[&_a]:underline [&_a]:underline-offset-2',
        )}
      />
      {format === 'markdown' ? (
        <p className="flex items-center gap-1.5 border-t border-border px-3 py-1.5 text-xs text-muted-foreground">
          {t('content.richText.markdownNote')}
          <InfoHint label={t('content.richText.markdownHintLabel')}>
            {t('content.richText.markdownHint')}
          </InfoHint>
        </p>
      ) : null}
    </div>
  );
}

/** One toolbar button. Pressed state is the editor's, so it is never out of step with it. */
function ToolButton({
  editor,
  label,
  active,
  disabled,
  onClick,
  children,
}: {
  editor: Editor;
  label: string;
  active?: boolean;
  disabled?: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      aria-pressed={active}
      disabled={disabled}
      // onMouseDown rather than onClick, and prevented: a click moves focus out of the
      // contenteditable first, which collapses the selection the command was meant to act on.
      onMouseDown={(e) => {
        e.preventDefault();
        onClick();
        editor.commands.focus();
      }}
      className={cn(
        'inline-flex h-7 w-7 items-center justify-center rounded text-muted-foreground',
        'hover:bg-accent hover:text-accent-foreground',
        'focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
        'disabled:pointer-events-none disabled:opacity-40',
        active && 'bg-accent text-accent-foreground',
      )}
    >
      {children}
    </button>
  );
}

function Toolbar({ editor, format }: { editor: Editor; format: RichFormat }) {
  const { t } = useTranslation();
  const icon = 'h-3.5 w-3.5';

  return (
    <div
      role="toolbar"
      aria-label={t('content.richText.toolbar')}
      className="flex flex-wrap items-center gap-0.5 border-b border-border px-1.5 py-1"
    >
      <ToolButton
        editor={editor}
        label={t('content.richText.bold')}
        active={editor.isActive('bold')}
        onClick={() => editor.chain().toggleBold().run()}
      >
        <Bold className={icon} aria-hidden />
      </ToolButton>
      <ToolButton
        editor={editor}
        label={t('content.richText.italic')}
        active={editor.isActive('italic')}
        onClick={() => editor.chain().toggleItalic().run()}
      >
        <Italic className={icon} aria-hidden />
      </ToolButton>
      <ToolButton
        editor={editor}
        label={t('content.richText.strike')}
        active={editor.isActive('strike')}
        onClick={() => editor.chain().toggleStrike().run()}
      >
        <Strikethrough className={icon} aria-hidden />
      </ToolButton>
      <ToolButton
        editor={editor}
        label={t('content.richText.code')}
        active={editor.isActive('code')}
        onClick={() => editor.chain().toggleCode().run()}
      >
        <Code className={icon} aria-hidden />
      </ToolButton>

      <Separator />

      {/*
        H2 and H3 only. An H1 belongs to the page, not to a field inside it — offering one
        here is how a site ends up with two top-level headings and a reader with no idea
        which is the title.
      */}
      <ToolButton
        editor={editor}
        label={t('content.richText.heading2')}
        active={editor.isActive('heading', { level: 2 })}
        onClick={() => editor.chain().toggleHeading({ level: 2 }).run()}
      >
        <Heading2 className={icon} aria-hidden />
      </ToolButton>
      <ToolButton
        editor={editor}
        label={t('content.richText.heading3')}
        active={editor.isActive('heading', { level: 3 })}
        onClick={() => editor.chain().toggleHeading({ level: 3 }).run()}
      >
        <Heading3 className={icon} aria-hidden />
      </ToolButton>

      <Separator />

      <ToolButton
        editor={editor}
        label={t('content.richText.bulletList')}
        active={editor.isActive('bulletList')}
        onClick={() => editor.chain().toggleBulletList().run()}
      >
        <List className={icon} aria-hidden />
      </ToolButton>
      <ToolButton
        editor={editor}
        label={t('content.richText.orderedList')}
        active={editor.isActive('orderedList')}
        onClick={() => editor.chain().toggleOrderedList().run()}
      >
        <ListOrdered className={icon} aria-hidden />
      </ToolButton>
      <ToolButton
        editor={editor}
        label={t('content.richText.quote')}
        active={editor.isActive('blockquote')}
        onClick={() => editor.chain().toggleBlockquote().run()}
      >
        <Quote className={icon} aria-hidden />
      </ToolButton>

      <Separator />

      <ToolButton
        editor={editor}
        label={t('content.richText.undo')}
        disabled={!editor.can().undo()}
        onClick={() => editor.chain().undo().run()}
      >
        <Undo2 className={icon} aria-hidden />
      </ToolButton>
      <ToolButton
        editor={editor}
        label={t('content.richText.redo')}
        disabled={!editor.can().redo()}
        onClick={() => editor.chain().redo().run()}
      >
        <Redo2 className={icon} aria-hidden />
      </ToolButton>

      <span className="ml-auto pr-1 font-mono text-[0.6875rem] uppercase tracking-wide text-muted-foreground">
        {format}
      </span>
    </div>
  );
}

function Separator() {
  return <span className="mx-0.5 h-4 w-px bg-border" aria-hidden />;
}
