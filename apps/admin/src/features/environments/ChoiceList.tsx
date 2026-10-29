import { Check } from 'lucide-react';
import { cn } from '@dcms/ui';

export interface Choice<T extends string> {
  value: T;
  title: React.ReactNode;
  description?: React.ReactNode;
  /** Why it cannot be chosen; shown in place of the description. */
  disabledReason?: React.ReactNode;
  aside?: React.ReactNode;
}

/**
 * One of several, each with a line saying what it means: the kind of a new environment, where a
 * link goes, which app it uses. A radio group drawn as rows, so the explanation is part of the choice.
 */
export function ChoiceList<T extends string>({
  label,
  value,
  onChange,
  choices,
  columns = 1,
}: {
  label: string;
  value: T | null;
  onChange: (value: T) => void;
  choices: readonly Choice<T>[];
  columns?: 1 | 2;
}) {
  return (
    <div role="radiogroup" aria-label={label} className={cn('grid grid-cols-1 gap-2', columns === 2 && 'sm:grid-cols-2')}>
      {choices.map((c) => {
        const selected = c.value === value;
        const disabled = c.disabledReason != null;
        return (
          <button
            key={c.value}
            type="button"
            role="radio"
            aria-checked={selected}
            disabled={disabled}
            onClick={() => onChange(c.value)}
            className={cn(
              'flex w-full items-start gap-3 rounded-lg border p-3 text-left transition-colors',
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
              selected ? 'border-primary bg-primary/5' : 'can-hover:hover:bg-accent/50',
              disabled && 'cursor-not-allowed opacity-60',
            )}
          >
            <span
              aria-hidden
              className={cn(
                'mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full border',
                selected ? 'border-primary bg-primary text-primary-foreground' : 'border-muted-foreground/40',
              )}
            >
              {selected ? <Check className="h-3 w-3" /> : null}
            </span>
            <span className="min-w-0 flex-1 [overflow-wrap:anywhere]">
              <span className="block text-sm font-medium">{c.title}</span>
              {disabled ? (
                <span className="mt-0.5 block text-xs text-muted-foreground">{c.disabledReason}</span>
              ) : c.description ? (
                <span className="mt-0.5 block text-xs text-muted-foreground">{c.description}</span>
              ) : null}
            </span>
            {c.aside ? <span className="shrink-0">{c.aside}</span> : null}
          </button>
        );
      })}
    </div>
  );
}
