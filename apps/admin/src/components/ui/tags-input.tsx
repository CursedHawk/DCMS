import { X } from 'lucide-react';
import { useRef, useState } from 'react';
import { cn } from '../../lib/cn';

/**
 * Coerce a stored value into a tag list. Accepts the array we write, a JSON
 * array left by an older editor, and a plain comma-separated string, so an
 * item authored before tags were a list still opens with its tags intact.
 */
export function toTagList(value: unknown): string[] {
  const raw = (() => {
    if (Array.isArray(value)) return value;
    if (typeof value !== 'string') return [];
    if (value.trim().startsWith('[')) {
      try {
        const parsed = JSON.parse(value);
        return Array.isArray(parsed) ? parsed : [];
      } catch {
        return value.split(',');
      }
    }
    return value.split(',');
  })();

  const out: string[] = [];
  for (const v of raw) {
    if (typeof v !== 'string') continue;
    const tag = v.trim();
    if (tag && !out.includes(tag)) out.push(tag);
  }
  return out;
}

/**
 * Multi-value tag entry: committed tags are chips, the input holds only the tag
 * being typed.
 *
 * Keeping the draft in local state is the point of this component. Binding the
 * input straight to `value.join(', ')` and re-parsing on every keystroke — which
 * is what this replaces — deletes the separator the moment it is typed, so no
 * second tag can ever be started.
 */
export function TagsInput({
  value,
  onChange,
  placeholder,
  disabled,
  className,
  id,
}: {
  value: string[];
  onChange: (tags: string[]) => void;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
  id?: string;
}) {
  const [draft, setDraft] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  /** Commits the draft (or pasted text) as one or more tags; ignores duplicates. */
  const commit = (text: string) => {
    const added = text.split(',').map((s) => s.trim());
    const next = value.slice();
    for (const tag of added) if (tag && !next.includes(tag)) next.push(tag);
    setDraft('');
    if (next.length !== value.length) onChange(next);
  };

  const removeAt = (i: number) => onChange(value.filter((_, j) => j !== i));

  return (
    <div
      className={cn(
        'flex min-h-9 w-full flex-wrap items-center gap-1.5 rounded-md border border-input bg-background px-2 py-1 text-sm shadow-sm',
        'focus-within:outline-none focus-within:ring-2 focus-within:ring-ring',
        disabled && 'cursor-not-allowed opacity-50',
        className,
      )}
      onClick={() => inputRef.current?.focus()}
    >
      {value.map((tag, i) => (
        <span
          key={tag}
          className="inline-flex items-center gap-1 rounded-full border border-transparent bg-secondary px-2 py-0.5 text-xs font-medium text-secondary-foreground"
        >
          {tag}
          <button
            type="button"
            aria-label={`Remove ${tag}`}
            disabled={disabled}
            className="text-muted-foreground hover:text-foreground disabled:pointer-events-none"
            onClick={(e) => {
              e.stopPropagation();
              removeAt(i);
            }}
          >
            <X className="h-3 w-3" />
          </button>
        </span>
      ))}

      <input
        ref={inputRef}
        id={id}
        value={draft}
        disabled={disabled}
        placeholder={value.length === 0 ? placeholder : undefined}
        className="h-7 min-w-24 flex-1 bg-transparent px-1 outline-none placeholder:text-muted-foreground disabled:cursor-not-allowed"
        onChange={(e) => {
          // A typed comma ends the current tag rather than living in the draft.
          if (e.target.value.includes(',')) commit(e.target.value);
          else setDraft(e.target.value);
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            // The editor is inside a dialog; committing a tag must not submit it.
            e.preventDefault();
            commit(draft);
          } else if (e.key === 'Backspace' && draft === '' && value.length > 0) {
            removeAt(value.length - 1);
          }
        }}
        // Losing focus with a half-typed tag keeps it, so a tag is never lost by
        // clicking Save instead of pressing Enter first.
        onBlur={() => draft.trim() && commit(draft)}
      />
    </div>
  );
}
