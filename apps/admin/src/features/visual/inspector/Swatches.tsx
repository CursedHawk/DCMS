import { THEME_JSON, themeTokensSchema, themeVariables } from '@dcms/site-runtime';
import { Check } from 'lucide-react';
import { useMemo } from 'react';
import { cn } from '@dcms/ui';
import { useVfs } from '../../site-source/vfs';

/** The theme variable (and the runtime's own fallback) each background choice paints with. */
const BACKGROUND_VARS: Readonly<Record<string, readonly [string, string]>> = {
  none: ['--dcms-color-surface', '#ffffff'],
  alt: ['--dcms-color-surface-alt', '#f8fafc'],
  soft: ['--dcms-color-brand-soft', '#eef2ff'],
  inverse: ['--dcms-color-inverse', '#0f172a'],
};

export function hasSwatches(name: string, values: readonly string[]): boolean {
  return name === 'background' && values.every((v) => v in BACKGROUND_VARS);
}

/**
 * A background choice as the colours themselves, taken from this site's theme — what the band
 * will actually look like with the current design kit, not a name to imagine it from.
 */
export function Swatches({
  id,
  label,
  value,
  options,
  onChange,
}: {
  id?: string;
  label: string;
  value: string;
  options: readonly { value: string; label: string }[];
  onChange: (value: string) => void;
}) {
  const themeText = useVfs((s) => s.files[THEME_JSON]);
  const vars = useMemo(() => {
    try {
      const theme = themeTokensSchema.safeParse(JSON.parse(themeText ?? '{}'));
      return new Map(theme.success ? themeVariables(theme.data).map((v) => [v.name, v.value]) : []);
    } catch {
      return new Map<string, string>();
    }
  }, [themeText]);
  return (
    <div id={id} role="radiogroup" aria-label={label} className="grid grid-cols-4 gap-2">
      {options.map((o) => {
        const [name, fallback] = BACKGROUND_VARS[o.value]!;
        const selected = o.value === value;
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={selected}
            onClick={() => onChange(o.value)}
            className="flex flex-col items-center gap-1 text-[11px] text-muted-foreground"
          >
            <span
              className={cn(
                'flex h-9 w-full items-center justify-center rounded-md border',
                selected ? 'ring-2 ring-primary ring-offset-1' : 'hover:border-primary/60',
              )}
              style={{ background: vars.get(name) ?? fallback }}
            >
              {selected && <Check className="h-4 w-4 text-primary mix-blend-difference" />}
            </span>
            <span className={cn('w-full truncate text-center', selected && 'font-medium text-foreground')}>{o.label}</span>
          </button>
        );
      })}
    </div>
  );
}
