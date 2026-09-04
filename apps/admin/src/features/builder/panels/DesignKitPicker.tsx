import {
  BLOCKS_CSS,
  DESIGN_KITS,
  applyKit,
  defaultKit,
  matchKit,
  missingThemeTokens,
  type DesignKit,
} from '@dcms/gjs-blocks';
import { Check, RefreshCw } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/admin-ui';
import { useBuilder } from '../store';

/**
 * The design kit picker.
 *
 * A kit is nothing but theme tokens — a palette, a type scale, a shadow ramp, a
 * set of metrics — so applying one rewrites the generated `styles/theme.css` and
 * touches nothing the author wrote. That is what makes trying six looks safe:
 * every kit is reversible by picking another, and the site's own
 * `styles/global.css` is never overwritten by the choice.
 *
 * The exception is the explicit refresh below, which *does* overwrite
 * `styles/global.css` — the block stylesheet is copied into a site when it is
 * seeded, so improvements to the library never reach an existing site on their
 * own. It is a separate, confirmed action for exactly that reason.
 */
export function DesignKitPicker() {
  const { t } = useTranslation();
  const project = useBuilder((s) => s.project);
  const [confirmingRefresh, setConfirmingRefresh] = useState(false);

  if (!project) return null;

  const current = matchKit(project.manifest.theme);
  const stale = project.globalCss !== BLOCKS_CSS;

  /**
   * Tokens the newer stylesheet reads that this site's theme has never defined.
   *
   * A site seeded before design kits existed defines seven colours and four
   * spacing steps; the current stylesheet reads nearly sixty tokens. Refreshing
   * the CSS without also giving it a theme that covers it would drop three
   * quarters of the declarations — no type scale, no radii, no shadows — with
   * nothing on screen to explain it. So the refresh carries a kit when it needs
   * one, rather than warning and doing it anyway.
   */
  const missing = missingThemeTokens(project.manifest.theme);
  const kitForRefresh = missing.length > 0 ? (current ?? defaultKit()) : undefined;

  const choose = (kit: DesignKit) => {
    useBuilder.getState().updateManifest((manifest) => ({
      ...manifest,
      theme: applyKit(manifest.theme, kit),
    }));
    // The canvas holds the old `:root` block; re-reading the page is what makes
    // the new tokens visible immediately rather than on the next page switch.
    useBuilder.getState().requestReload();
  };

  const refreshBlockStyles = () => {
    useBuilder.getState().update((p) => ({
      ...p,
      globalCss: BLOCKS_CSS,
      manifest: kitForRefresh
        ? { ...p.manifest, theme: applyKit(p.manifest.theme, kitForRefresh) }
        : p.manifest,
    }));
    useBuilder.getState().requestReload();
    setConfirmingRefresh(false);
  };

  return (
    <section className="space-y-2">
      <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {t('builder.designKit')}
      </h3>
      <p className="text-xs text-muted-foreground">{t('builder.designKitHint')}</p>

      <div className="grid grid-cols-2 gap-2">
        {DESIGN_KITS.map((kit) => {
          const active = current?.id === kit.id;
          return (
            <button
              key={kit.id}
              type="button"
              onClick={() => choose(kit)}
              title={kit.description}
              aria-pressed={active}
              className={cn(
                'relative overflow-hidden rounded-md border p-2 text-left transition-colors',
                active ? 'border-primary ring-1 ring-primary' : 'hover:border-primary/50 hover:bg-accent/40',
              )}
              style={{ background: kit.preview.surface, color: kit.preview.text }}
            >
              {active && (
                <Check className="absolute right-1.5 top-1.5 h-3.5 w-3.5 text-primary" aria-hidden />
              )}
              <span
                className="block truncate text-sm font-semibold leading-tight"
                style={{ fontFamily: kit.preview.headingFont }}
              >
                {kit.name}
              </span>
              <span className="mt-1.5 flex gap-1">
                {kit.preview.swatches.map((colour, i) => (
                  <span
                    key={`${kit.id}-${i}`}
                    className="h-4 w-4 border border-black/10"
                    style={{ background: colour, borderRadius: kit.preview.radius }}
                  />
                ))}
              </span>
            </button>
          );
        })}
      </div>

      {current === undefined && (
        <p className="text-xs text-muted-foreground">{t('builder.designKitCustom')}</p>
      )}

      {stale &&
        (confirmingRefresh ? (
          <div className="space-y-1.5 rounded-md border border-amber-500/40 bg-amber-500/5 p-2">
            <p className="text-xs">{t('builder.refreshBlockStylesConfirm')}</p>
            {kitForRefresh && (
              <p className="text-xs">
                {t('builder.refreshBlockStylesNeedsKit', {
                  count: missing.length,
                  kit: kitForRefresh.name,
                })}
              </p>
            )}
            <div className="flex gap-1.5">
              <button
                type="button"
                onClick={refreshBlockStyles}
                className="rounded border bg-background px-2 py-1 text-xs font-medium hover:bg-accent"
              >
                {t('builder.refreshBlockStyles')}
              </button>
              <button
                type="button"
                onClick={() => setConfirmingRefresh(false)}
                className="rounded px-2 py-1 text-xs text-muted-foreground hover:bg-accent"
              >
                {t('actions.cancel')}
              </button>
            </div>
          </div>
        ) : (
          <button
            type="button"
            onClick={() => setConfirmingRefresh(true)}
            className="flex items-center gap-1.5 text-xs text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
          >
            <RefreshCw className="h-3 w-3" />
            {t('builder.refreshBlockStyles')}
          </button>
        ))}
    </section>
  );
}
