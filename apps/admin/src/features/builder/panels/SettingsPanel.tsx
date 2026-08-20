import { defaultLayout, type CookieConsentSettings, type ThemeTokens } from '@dcms/gjs-schema';
import { useTranslation } from 'react-i18next';
import { Input, Textarea } from '../../../components/ui/input';
import { useBuilder } from '../store';
import { DesignKitPicker } from './DesignKitPicker';

/**
 * Page and site settings — the parts of `site.json` that are not markup.
 *
 * Editing them here writes straight into the manifest, which the store diffs
 * into the working draft, so a title change shows up in Source Control as a
 * one-line `site.json` diff rather than as an opaque blob rewrite.
 */
export function SettingsPanel() {
  const { t } = useTranslation();
  const project = useBuilder((s) => s.project);
  const activeSlug = useBuilder((s) => s.activeSlug);
  const activeKind = useBuilder((s) => s.activeKind);
  const page = activeKind === 'page' ? project?.pages.find((p) => p.entry.slug === activeSlug)?.entry : undefined;

  if (!project || !page) {
    // A region has no page settings — no path, no SEO, no layout of its own —
    // so saying "no project" here would be a lie about why the panel is empty.
    const message = activeKind === 'region' ? t('builder.regions.settingsHint') : t('builder.noProject');
    return <p className="p-4 text-sm text-muted-foreground">{message}</p>;
  }

  type PagePatch = Partial<Omit<typeof page, 'seo'>> & { seo?: Partial<typeof page.seo> };

  const patchPage = (patch: PagePatch) => {
    useBuilder.getState().update((current) => ({
      ...current,
      manifest: {
        ...current.manifest,
        pages: current.manifest.pages.map((p) =>
          p.slug === page.slug ? { ...p, ...patch, seo: { ...p.seo, ...(patch.seo ?? {}) } } : p,
        ),
      },
      pages: current.pages.map((p) =>
        p.entry.slug === page.slug
          ? { ...p, entry: { ...p.entry, ...patch, seo: { ...p.entry.seo, ...(patch.seo ?? {}) } } }
          : p,
      ),
    }));
  };

  const consent: CookieConsentSettings = project.manifest.settings.cookieConsent ?? { mode: 'banner' };

  const patchConsent = (patch: Partial<CookieConsentSettings>) => {
    useBuilder.getState().updateManifest((manifest) => ({
      ...manifest,
      settings: {
        ...manifest.settings,
        cookieConsent: { ...(manifest.settings.cookieConsent ?? { mode: 'banner' }), ...patch },
      },
    }));
  };

  const patchTheme = (patch: Partial<ThemeTokens>) => {
    useBuilder.getState().updateManifest((manifest) => ({
      ...manifest,
      theme: { ...manifest.theme, ...patch },
    }));
  };

  return (
    <div className="space-y-5 p-3">
      <section className="space-y-2">
        <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {t('builder.pageSettings')}
        </h3>
        <Field label={t('builder.pageTitle')}>
          <Input value={page.title} onChange={(e) => patchPage({ title: e.target.value })} className="h-8" />
        </Field>
        <Field label={t('builder.pagePath')}>
          <Input
            value={page.path}
            onChange={(e) => patchPage({ path: e.target.value })}
            className="h-8"
            // The slug (and therefore the file names) is deliberately not edited
            // here: renaming files would turn a title tweak into a git move.
          />
        </Field>
        <Field label={t('builder.seoTitle')}>
          <Input
            value={page.seo.title}
            onChange={(e) => patchPage({ seo: { title: e.target.value } })}
            className="h-8"
          />
        </Field>
        <Field label={t('builder.seoDescription')}>
          <Textarea
            rows={3}
            value={page.seo.description ?? ''}
            onChange={(e) => patchPage({ seo: { description: e.target.value } })}
          />
        </Field>
        {/* Which shared chrome wraps this page. Empty string is a real value —
            "no chrome at all" — and is not the same as leaving it unset, which
            means "whatever the site's default layout is". A landing page that
            must not show the site navigation is the reason both exist. */}
        <Field label={t('builder.regions.pageLayout')}>
          <select
            value={page.layout ?? '#default'}
            onChange={(e) =>
              patchPage({ layout: e.target.value === '#default' ? undefined : e.target.value })
            }
            className="h-8 w-full rounded-md border bg-background px-2 text-sm"
          >
            <option value="#default">
              {t('builder.regions.layoutDefault', {
                label: defaultLayout(project.manifest)?.label ?? t('builder.regions.noneOption'),
              })}
            </option>
            {project.manifest.layouts.map((layout) => (
              <option key={layout.id} value={layout.id}>
                {layout.label}
              </option>
            ))}
            <option value="">{t('builder.regions.noneOption')}</option>
          </select>
        </Field>

        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={page.seo.noIndex ?? false}
            onChange={(e) => patchPage({ seo: { noIndex: e.target.checked } })}
            className="h-4 w-4"
          />
          {t('builder.noIndex')}
        </label>
      </section>

      {/*
        Cookie consent is a *site* setting, not a platform one: the obligation is
        the site owner's and depends on their audience, and the wording has to be
        theirs. It defaults to showing a banner, so a site nobody has configured
        does not silently track its visitors.
      */}
      <section className="space-y-2 border-t pt-4">
        <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {t('builder.consent.title')}
        </h3>
        <p className="text-xs text-muted-foreground">{t('builder.consent.hint')}</p>

        <Field label={t('builder.consent.mode')}>
          <select
            value={consent.mode}
            onChange={(e) => patchConsent({ mode: e.target.value as CookieConsentSettings['mode'] })}
            className="h-8 w-full rounded-md border bg-background px-2 text-sm"
          >
            <option value="banner">{t('builder.consent.modeBanner')}</option>
            <option value="off">{t('builder.consent.modeOff')}</option>
          </select>
        </Field>

        {consent.mode === 'banner' ? (
          <>
            <Field label={t('builder.consent.message')}>
              <Textarea
                rows={3}
                value={consent.message ?? ''}
                placeholder={t('builder.consent.messageDefault')}
                onChange={(e) => patchConsent({ message: e.target.value || undefined })}
              />
            </Field>
            <Field label={t('builder.consent.acceptLabel')}>
              <Input
                value={consent.acceptLabel ?? ''}
                placeholder="Accept"
                onChange={(e) => patchConsent({ acceptLabel: e.target.value || undefined })}
                className="h-8"
              />
            </Field>
            <Field label={t('builder.consent.declineLabel')}>
              <Input
                value={consent.declineLabel ?? ''}
                placeholder="Decline"
                onChange={(e) => patchConsent({ declineLabel: e.target.value || undefined })}
                className="h-8"
              />
            </Field>
            <Field label={t('builder.consent.policyUrl')}>
              <Input
                value={consent.policyUrl ?? ''}
                placeholder="/privacy"
                onChange={(e) => patchConsent({ policyUrl: e.target.value || undefined })}
                className="h-8"
              />
            </Field>
          </>
        ) : (
          <p className="rounded-md border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 p-2 text-xs">
            {t('builder.consent.offWarning')}
          </p>
        )}
      </section>

      <div className="border-t pt-4">
        <DesignKitPicker />
      </div>

      <section className="space-y-2 border-t pt-4">
        <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {t('builder.theme')}
        </h3>
        <p className="text-xs text-muted-foreground">{t('builder.themeHint')}</p>

        {/* Collapsed by default: a kit defines twenty-odd colour tokens, and an
            author who wants a different look should reach for the picker above
            rather than scroll a wall of colour inputs to find `brand`. */}
        <details className="group space-y-2">
          <summary className="cursor-pointer list-none text-xs font-medium text-muted-foreground hover:text-foreground">
            {t('builder.themeTokens')}
          </summary>

        {Object.entries(project.manifest.theme.colors).map(([token, value]) => (
          <Field key={token} label={token}>
            <span className="flex items-center gap-1">
              <input
                type="color"
                value={/^#[0-9a-f]{6}$/i.test(value) ? value : '#000000'}
                onChange={(e) =>
                  patchTheme({ colors: { ...project.manifest.theme.colors, [token]: e.target.value } })
                }
                className="h-8 w-8 shrink-0 cursor-pointer rounded border bg-background p-0.5"
              />
              <Input
                value={value}
                onChange={(e) =>
                  patchTheme({ colors: { ...project.manifest.theme.colors, [token]: e.target.value } })
                }
                className="h-8 min-w-0 flex-1"
              />
            </span>
          </Field>
        ))}

        {Object.entries(project.manifest.theme.fonts).map(([token, value]) => (
          <Field key={token} label={token}>
            <Input
              value={value}
              onChange={(e) =>
                patchTheme({ fonts: { ...project.manifest.theme.fonts, [token]: e.target.value } })
              }
              className="h-8"
            />
          </Field>
        ))}

          <Field label={t('builder.radius')}>
            <Input
              value={project.manifest.theme.radius ?? ''}
              onChange={(e) => patchTheme({ radius: e.target.value })}
              className="h-8"
            />
          </Field>
        </details>
      </section>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block space-y-1">
      <span className="text-xs font-medium">{label}</span>
      {children}
    </label>
  );
}
