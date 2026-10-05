import { DESIGN_KITS, matchKit, type DesignKit } from '@dcms/gjs-blocks';
import { THEME_JSON, themeTokensSchema, type ThemeTokens } from '@dcms/site-runtime';
import { AlertTriangle, Check, RotateCcw } from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, cn } from '@dcms/ui';
import { HelpLink } from './help/HelpLink';
import { useVfs } from '../site-source';
import { Segmented } from './inspector/Segmented';
import { serializeDoc } from './starter';
import { DENSITY, FONTS, ROUNDNESS, SHADOW, contrast, tuneTheme, type Tuning } from './theme/tune';

/**
 * The site's look: a design kit first — a complete set of theme tokens (ADR 0020, the same
 * vocabulary Mode A uses), so one choice restyles every component consistently — then a few
 * adjustments on top of it (Mode D v2, U3.5): brand and accent colours, fonts, corners, air and
 * shadows. Adjustments are always recomputed from the kit, so "reset" is exact. There is no raw
 * CSS in the basic builder.
 */
export function ThemePanel() {
  const { t } = useTranslation();
  const text = useVfs((s) => s.files[THEME_JSON]);
  const theme = useMemo<ThemeTokens | undefined>(() => {
    try {
      const parsed = themeTokensSchema.safeParse(JSON.parse(text ?? ''));
      return parsed.success ? parsed.data : undefined;
    } catch {
      return undefined;
    }
  }, [text]);
  const kit = (theme?.kit && DESIGN_KITS.find((k) => k.id === theme.kit)) || (theme && matchKit(theme)) || undefined;
  const tuning: Tuning = theme?.tuning ?? {};

  const write = (next: DesignKit, nextTuning: Tuning) =>
    useVfs.getState().writeFile(THEME_JSON, serializeDoc(tuneTheme(next, nextTuning, theme?.custom ?? {})));
  const tune = (patch: Tuning) => kit && write(kit, { ...tuning, ...patch });

  return (
    <div className="flex h-full flex-col overflow-y-auto">
      <div className="flex items-center gap-1.5 border-b px-3 py-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        {t('visual.theme.title')} <HelpLink article="theme" />
      </div>
      <p className="px-3 pt-3 text-xs text-muted-foreground">{t('visual.theme.hint')}</p>
      <ul className="space-y-2 p-3">
        {DESIGN_KITS.map((k) => {
          const active = kit?.id === k.id;
          const colors = k.theme.colors;
          return (
            <li key={k.id}>
              <button
                type="button"
                aria-pressed={active}
                onClick={() => write(k, {})}
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
                    {k.name} {active && <Check className="h-3.5 w-3.5 text-primary" />}
                  </span>
                  <span className="block text-xs text-muted-foreground">{k.description}</span>
                </span>
              </button>
            </li>
          );
        })}
      </ul>

      {kit && theme && (
        <section aria-labelledby="theme-tune" className="space-y-3 border-t p-3">
          <div className="flex items-center justify-between">
            <h3 id="theme-tune" className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
              {t('visual.theme.tune', { kit: kit.name })}
            </h3>
            {theme.tuning && (
              <Button size="sm" variant="ghost" onClick={() => write(kit, {})}>
                <RotateCcw className="h-3.5 w-3.5" /> {t('visual.theme.reset')}
              </Button>
            )}
          </div>
          <ColourField
            id="theme-brand"
            label={t('visual.theme.brand')}
            value={theme.colors.brand ?? '#000000'}
            onChange={(brand) => tune({ brand })}
            warning={contrast(theme.colors.brand ?? '', theme.colors.surface ?? '#ffffff') < 3 ? t('visual.theme.lowContrast') : undefined}
          />
          <ColourField
            id="theme-accent"
            label={t('visual.theme.accent')}
            value={theme.colors.accent ?? '#000000'}
            onChange={(accent) => tune({ accent })}
          />
          <FontField id="theme-heading-font" label={t('visual.theme.headingFont')} value={tuning.headingFont} onChange={(headingFont) => tune({ headingFont })} />
          <FontField id="theme-body-font" label={t('visual.theme.bodyFont')} value={tuning.bodyFont} onChange={(bodyFont) => tune({ bodyFont })} />
          <Steps label={t('visual.theme.roundness')} steps={ROUNDNESS} value={tuning.roundness} names={t('visual.theme.roundnessSteps').split('|')} onChange={(roundness) => tune({ roundness })} />
          <Steps label={t('visual.theme.density')} steps={DENSITY} value={tuning.density} names={t('visual.theme.densitySteps').split('|')} onChange={(density) => tune({ density })} />
          <Steps label={t('visual.theme.shadow')} steps={SHADOW} value={tuning.shadow} names={t('visual.theme.shadowSteps').split('|')} onChange={(shadow) => tune({ shadow })} />
        </section>
      )}
    </div>
  );
}

function ColourField({ id, label, value, onChange, warning }: { id: string; label: string; value: string; onChange: (v: string) => void; warning?: string }) {
  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{label}</Label>
      <div className="flex items-center gap-2">
        <input id={id} type="color" value={value} onChange={(e) => onChange(e.target.value)} className="h-8 w-12 cursor-pointer rounded border bg-background p-0.5" />
        <code className="text-xs text-muted-foreground">{value}</code>
      </div>
      {warning && (
        <p className="flex items-start gap-1 text-xs text-amber-600 dark:text-amber-400">
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" /> {warning}
        </p>
      )}
    </div>
  );
}

function FontField({ id, label, value, onChange }: { id: string; label: string; value?: string; onChange: (v: string | undefined) => void }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{label}</Label>
      <Select value={value ?? 'kit'} onValueChange={(v) => onChange(v === 'kit' ? undefined : v)}>
        <SelectTrigger id={id}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="kit">{t('visual.theme.kitFont')}</SelectItem>
          {FONTS.map((f) => (
            <SelectItem key={f.id} value={f.id}>
              <span style={{ fontFamily: f.stack }}>{f.label}</span>
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}

/** A scale whose middle-ish step (1) is the kit as designed. */
function Steps({ label, steps, value, names, onChange }: { label: string; steps: readonly number[]; value?: number; names: string[]; onChange: (v: number) => void }) {
  return (
    <Segmented
      label={label}
      value={String(value ?? 1)}
      options={steps.map((s, i) => ({ value: String(s), label: names[i] ?? String(s) }))}
      onChange={(v) => onChange(Number(v))}
    />
  );
}
