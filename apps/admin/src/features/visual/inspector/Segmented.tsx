import type { LucideIcon } from 'lucide-react';
import { useRef } from 'react';
import { cn } from '@dcms/ui';

/**
 * A short choice shown whole: every option visible, one click to pick. A radio group to
 * assistive technology, with arrow keys moving the choice the way native radios do.
 */
export function Segmented({
  id,
  label,
  value,
  options,
  onChange,
}: {
  id?: string;
  label: string;
  value: string;
  options: readonly { value: string; label: string; icon?: LucideIcon }[];
  onChange: (value: string) => void;
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const current = Math.max(0, options.findIndex((o) => o.value === value));
  const move = (to: number) => {
    const next = (to + options.length) % options.length;
    onChange(options[next]!.value);
    refs.current[next]?.focus();
  };
  return (
    <div id={id} role="radiogroup" aria-label={label} className="flex w-full rounded-md border bg-muted/40 p-0.5">
      {options.map((o, i) => {
        const selected = o.value === value;
        const Icon = o.icon;
        return (
          <button
            key={o.value}
            ref={(el) => {
              refs.current[i] = el;
            }}
            type="button"
            role="radio"
            aria-checked={selected}
            aria-label={Icon ? o.label : undefined}
            title={Icon ? o.label : undefined}
            tabIndex={i === current ? 0 : -1}
            onClick={() => onChange(o.value)}
            onKeyDown={(e) => {
              if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
                e.preventDefault();
                move(i + 1);
              } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
                e.preventDefault();
                move(i - 1);
              }
            }}
            className={cn(
              'flex min-w-0 flex-1 items-center justify-center rounded px-1.5 py-1 text-xs transition-colors',
              selected ? 'bg-background font-medium text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {Icon ? <Icon className="h-4 w-4" /> : <span className="truncate">{o.label}</span>}
          </button>
        );
      })}
    </div>
  );
}
