import { Paperclip, Send, Square, X } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, cn } from '@dcms/ui';
import type { Attachment } from './attachments';
import { ModeSwitch } from './ModeSwitch';
import type { AiMode } from './modes';

/**
 * Where a question is written.
 *
 * <p>A textarea rather than an input: people ask an agent for a paragraph of work, and a
 * single-line box that scrolls sideways tells them not to. Enter sends, Shift+Enter breaks the
 * line — the convention every chat surface has taught, and getting it backwards is how a
 * half-written instruction gets sent.</p>
 */
export function Composer({
  onSend,
  onStop,
  running,
  mode,
  onModeChange,
  writable,
  attachments,
  onAttach,
  onDetach,
  toolCount,
  unsaved,
  placeholder,
}: {
  onSend: (text: string) => void;
  onStop: () => void;
  running: boolean;
  mode: AiMode;
  onModeChange: (mode: AiMode) => void;
  writable: boolean;
  attachments: readonly Attachment[];
  onAttach: (files: readonly File[]) => void;
  onDetach: (name: string) => void;
  toolCount: number;
  /** True when the last append to the stored conversation failed. */
  unsaved?: boolean;
  placeholder?: string;
}) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState('');
  const fileInput = useRef<HTMLInputElement>(null);

  const submit = () => {
    if (running) return;
    if (!draft.trim() && attachments.length === 0) return;
    onSend(draft);
    setDraft('');
  };

  return (
    <div className="space-y-2">
      {attachments.length > 0 ? (
        <ul className="flex flex-wrap gap-1.5">
          {attachments.map((file) => (
            <li
              key={file.name}
              className={cn(
                'inline-flex items-center gap-1 rounded-full border px-2 py-0.5 font-mono text-[11px]',
                file.assetId && 'border-[hsl(var(--success)/0.4)] text-[hsl(var(--success))]',
              )}
            >
              {file.name}
              <button
                type="button"
                onClick={() => onDetach(file.name)}
                aria-label={t('assistant.removeFile', { name: file.name })}
                className="text-muted-foreground hover:text-foreground"
              >
                <X className="h-3 w-3" aria-hidden />
              </button>
            </li>
          ))}
        </ul>
      ) : null}

      <form
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
        className="rounded-md border bg-background focus-within:ring-2 focus-within:ring-ring"
      >
        <textarea
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
              e.preventDefault();
              submit();
            }
          }}
          rows={2}
          placeholder={placeholder ?? t('assistant.placeholder')}
          aria-label={placeholder ?? t('assistant.placeholder')}
          className="max-h-40 w-full resize-y bg-transparent px-3 py-2 text-sm outline-none placeholder:text-muted-foreground"
        />

        <div className="flex items-center gap-1 border-t px-1.5 py-1">
          <input
            ref={fileInput}
            type="file"
            multiple
            hidden
            onChange={(e) => {
              onAttach(Array.from(e.target.files ?? []));
              // Cleared so re-picking the same file fires change again.
              e.target.value = '';
            }}
          />
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className="h-7 w-7"
            onClick={() => fileInput.current?.click()}
            aria-label={t('assistant.attach')}
            title={t('assistant.attach')}
          >
            <Paperclip className="h-3.5 w-3.5" aria-hidden />
          </Button>

          <ModeSwitch mode={mode} onChange={onModeChange} disabled={!writable} />

          <span className="ml-auto" />

          {running ? (
            <Button
              type="button"
              variant="outline"
              size="icon"
              className="h-7 w-7"
              onClick={onStop}
              aria-label={t('assistant.stop')}
            >
              <Square className="h-3.5 w-3.5" aria-hidden />
            </Button>
          ) : (
            <Button
              type="submit"
              size="icon"
              className="h-7 w-7"
              disabled={!draft.trim() && attachments.length === 0}
              aria-label={t('assistant.send')}
            >
              <Send className="h-3.5 w-3.5" aria-hidden />
            </Button>
          )}
        </div>
      </form>

      {/* Says what it can reach, in the operator's terms. An assistant whose limits are
          invisible gets asked for things it will never manage, once each. */}
      <p className="text-xs text-muted-foreground">
        {unsaved
          ? t('assistant.unsaved')
          : mode === 'read'
            ? t('assistant.toolCount', { count: toolCount })
            : t('assistant.toolCountWrite', { count: toolCount })}
      </p>
    </div>
  );
}
