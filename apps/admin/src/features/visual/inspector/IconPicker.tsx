import { ICON_NAMES, IconGlyph } from '@dcms/site-runtime';
import { ChevronDown } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Input, Popover, PopoverContent, PopoverTrigger, cn } from '@dcms/ui';

/** The icon a setting holds, picked by sight from the set the site runtime embeds. */
export function IconPicker({ id, label, value, onChange }: { id: string; label: string; value: string; onChange: (name: string) => void }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const names = ICON_NAMES.filter((n) => n.includes(query.trim().toLowerCase().replace(/\s+/g, '-')));
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          id={id}
          type="button"
          aria-label={`${label}: ${value || t('visual.icons.none')}`}
          className="flex h-9 w-full items-center gap-2 rounded-md border bg-background px-3 text-sm hover:bg-accent/40"
        >
          {value && <IconGlyph name={value} className="h-4 w-4" />}
          <span className="min-w-0 flex-1 truncate text-left">{value || t('visual.icons.choose')}</span>
          <ChevronDown className="h-4 w-4 opacity-60" />
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-72 p-2" align="start">
        <Input value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t('visual.icons.search')} aria-label={t('visual.icons.search')} />
        <div role="listbox" aria-label={label} className="mt-2 grid max-h-64 grid-cols-6 gap-1 overflow-y-auto">
          {names.map((name) => (
            <button
              key={name}
              type="button"
              role="option"
              aria-selected={name === value}
              title={name}
              onClick={() => {
                onChange(name);
                setOpen(false);
              }}
              className={cn('flex aspect-square items-center justify-center rounded hover:bg-accent', name === value && 'bg-primary/10 text-primary')}
            >
              <IconGlyph name={name} label={name} className="h-5 w-5" />
            </button>
          ))}
          {names.length === 0 && <p className="col-span-6 p-2 text-xs text-muted-foreground">{t('visual.icons.noMatch')}</p>}
        </div>
      </PopoverContent>
    </Popover>
  );
}
