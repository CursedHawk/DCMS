import { ArrowDown, ArrowUp, Columns3 } from 'lucide-react';
import { Button } from '../ui/button';
import { Checkbox } from '../ui/checkbox';
import { Popover, PopoverContent, PopoverTrigger } from '../ui/popover';
import { DEFAULT_VIEW_LABELS, type ViewLabels } from './labels';
import type { ViewField } from './model';

/**
 * Which columns a view shows, and in what order (#278): the shown ones first, in order, with
 * buttons to move them — a keyboard can reorder, a drag handle alone could not.
 */
export function ColumnChooser({
  fields,
  value,
  onChange,
  labels: partial,
}: {
  fields: readonly ViewField[];
  /** Shown columns' keys, in order. */
  value: readonly string[];
  onChange: (next: string[]) => void;
  labels?: Partial<ViewLabels>;
}) {
  const labels = { ...DEFAULT_VIEW_LABELS, ...partial };
  const shown = value.map((k) => fields.find((f) => f.key === k)).filter((f): f is ViewField => !!f);
  const hidden = fields.filter((f) => !value.includes(f.key));
  const move = (index: number, by: -1 | 1) => {
    const next = [...value];
    [next[index], next[index + by]] = [next[index + by], next[index]];
    onChange(next);
  };

  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button size="sm" variant="outline">
          <Columns3 className="h-4 w-4" /> {labels.columns}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="max-h-[70vh] w-72 overflow-y-auto p-2">
        <ul className="space-y-0.5" aria-label={labels.columns}>
          {[...shown, ...hidden].map((f) => {
            const index = value.indexOf(f.key);
            const on = index >= 0;
            return (
              <li key={f.key} className="flex min-w-0 items-center gap-2 rounded px-1 py-1 hover:bg-accent/50">
                <Checkbox
                  id={`col-${f.key}`}
                  checked={on}
                  aria-label={labels.showColumn(f.label)}
                  onCheckedChange={(c) => onChange(c === true ? [...value, f.key] : value.filter((k) => k !== f.key))}
                />
                <label htmlFor={`col-${f.key}`} className="min-w-0 flex-1 text-sm [overflow-wrap:anywhere]">
                  {f.label}
                </label>
                {on && (
                  <span className="flex shrink-0">
                    <Button size="icon" variant="ghost" className="h-6 w-6" aria-label={labels.moveUp(f.label)} disabled={index === 0} onClick={() => move(index, -1)}>
                      <ArrowUp className="h-3.5 w-3.5" />
                    </Button>
                    <Button
                      size="icon"
                      variant="ghost"
                      className="h-6 w-6"
                      aria-label={labels.moveDown(f.label)}
                      disabled={index === value.length - 1}
                      onClick={() => move(index, 1)}
                    >
                      <ArrowDown className="h-3.5 w-3.5" />
                    </Button>
                  </span>
                )}
              </li>
            );
          })}
        </ul>
      </PopoverContent>
    </Popover>
  );
}
