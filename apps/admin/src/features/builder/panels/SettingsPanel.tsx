import type { ThemeTokens } from '@dcms/gjs-schema';
import { useTranslation } from 'react-i18next';
import { Input, Textarea } from '../../../components/ui/input';
import { useBuilder } from '../store';

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
  const page = project?.pages.find((p) => p.entry.slug === activeSlug)?.entry;

  if (!project || !page) {
    return <p className="p-4 text-sm text-muted-foreground">{t('builder.noProject')}</p>;
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

      <section className="space-y-2 border-t pt-4">
        <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {t('builder.theme')}
        </h3>
        <p className="text-xs text-muted-foreground">{t('builder.themeHint')}</p>

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
