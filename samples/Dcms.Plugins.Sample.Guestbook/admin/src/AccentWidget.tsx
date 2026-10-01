import type { PluginWidgetProps } from '@dcms/plugin-ui';
import { usePluginT } from '@dcms/plugin-ui';
import { ACCENTS, accentOf, type Accent } from './accents';

/**
 * A config widget: the guestbook's `"format": "guestbook-accent"` field, drawn as swatches
 * instead of a dropdown of colour names. Registered in index.tsx under that format; the console
 * routes the field here on the plugin's settings and install forms.
 */
export function AccentWidget({ id, value, onChange, disabled }: PluginWidgetProps) {
  const { t } = usePluginT();
  const current = accentOf(value);
  return (
    <div role="radiogroup" aria-label={t('accent.label')} id={id} className="gb-swatches">
      {(Object.keys(ACCENTS) as Accent[]).map((accent) => (
        <button
          key={accent}
          type="button"
          role="radio"
          aria-checked={accent === current}
          disabled={disabled}
          className="gb-swatch"
          style={{ ['--gb-ribbon' as string]: ACCENTS[accent].ribbon }}
          onClick={() => onChange(accent)}
        >
          <span className="gb-swatch-dot" aria-hidden />
          {t(`accent.${accent}`)}
        </button>
      ))}
    </div>
  );
}
