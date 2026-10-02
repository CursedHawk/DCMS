import { DESIGN_KITS, matchKit } from '@dcms/gjs-blocks';
import { THEME_JSON, themeTokensSchema } from '@dcms/site-runtime';
import { Check } from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { useVfs } from '../site-source';
import { serializeDoc } from './starter';

/**
 * The site's look, as one choice: a design kit is a complete set of theme tokens (ADR 0020 —
 * the same vocabulary Mode A uses), so applying one restyles every component consistently and
 * cannot leave half the tokens undefined the way a hand-written theme does. Components expose
 * only semantic choices (spacing, background, variant) that resolve to these tokens; there is no
 * raw CSS in the basic builder.
 */
export function ThemePanel() {
  const { t } = useTranslation();
  const text = useVfs((s) => s.files[THEME_JSON]);
  const current = useMemo(() => {
    try {
      const theme = themeTokensSchema.safeParse(JSON.parse(text ?? ''));
      return theme.success ? matchKit(theme.data) : undefined;
    } catch {
      return undefined;
    }
  }, [text]);

  return (
    <div className="flex h-full flex-col overflow-y-auto">
      <div className="border-b px-3 py-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        {t('visual.theme.title')}
      </div>
      <p className="px-3 pt-3 text-xs text-muted-foreground">{t('visual.theme.hint')}</p>
      <ul className="space-y-2 p-3">
        {DESIGN_KITS.map((kit) => {
          const active = current?.id === kit.id;
          const colors = kit.theme.colors;
          return (
            <li key={kit.id}>
              <button
                type="button"
                aria-pressed={active}
                onClick={() => useVfs.getState().writeFile(THEME_JSON, serializeDoc(kit.theme))}
                className={cn(
                  'flex w-full items-start gap-3 rounded-md border p-3 text-left hover:bg-muted/60',
                  active && 'border-primary ring-1 ring-primary',
                )}
              >
                <span className="flex shrink-0 -space-x-1 pt-0.5" aria-hidden>
                  {[colors.brand, colors.surface, colors.text, colors.accent].map((c, i) => (
                    <span key={i} className="h-5 w-5 rounded-full border" style={{ background: c }} />
                  ))}
                </span>
                <span className="min-w-0 flex-1">
                  <span className="flex items-center gap-1 text-sm font-medium">
                    {kit.name} {active && <Check className="h-3.5 w-3.5 text-primary" />}
                  </span>
                  <span className="block text-xs text-muted-foreground">{kit.description}</span>
                </span>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
