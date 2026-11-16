import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ExternalLink, Plus, X } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Callout, Input, Switch, toast, toastApiError } from '@dcms/ui';
import { api } from '../../lib/api';
import { ManagedNotice } from '../environments/ManagedNotice';
import { useManaged } from '../environments/api';
import { hiddenBy, pathProblem, setPageShown, type SearchSettings, type SearchView } from './searchRules';

/**
 * What search engines may see of a site (#648), in every site editor's rail: the URLs its sitemap
 * lists, paths hidden by rule, and whether the site is indexed at all. A Mode D site also lists its
 * own pages, shown here each with a switch. Saved in one go, and live without a rebuild.
 */
export function SearchEnginesView({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const key = ['site-search', siteId];
  const view = useQuery({ queryKey: key, queryFn: () => api.get<SearchView>(`/admin/sites/${siteId}/search`) });
  const { managed } = useManaged('sites');
  const [draft, setDraft] = useState<SearchSettings | null>(null);

  const save = useMutation({
    mutationFn: (settings: SearchSettings) => api.put<SearchSettings>(`/admin/sites/${siteId}/search`, settings),
    onSuccess: (saved) => {
      toast.success(t('searchEngines.saved'));
      queryClient.setQueryData<SearchView>(key, (old) => (old ? { ...old, settings: saved } : old));
      setDraft(null);
    },
    onError: (e) => toastApiError(e, t),
  });

  if (view.isLoading) return <p className="p-3 text-xs text-muted-foreground">{t('common.loading')}</p>;
  if (!view.data) return <p className="p-3 text-xs text-destructive">{t('errors.generic')}</p>;

  const settings = draft ?? view.data.settings;
  const locked = managed(view.data.environmentKey);
  const change = (next: SearchSettings) => setDraft(next);

  return (
    <div className="flex h-full flex-col overflow-auto">
      <div className="flex shrink-0 items-center gap-2 border-b p-2">
        <span className="flex-1 text-2xs font-semibold uppercase tracking-wide text-muted-foreground">
          {t('searchEngines.title')}
        </span>
        <Button size="sm" onClick={() => save.mutate(settings)} disabled={!draft || locked || save.isPending}>
          {t('searchEngines.save')}
        </Button>
      </div>

      <div className="space-y-4 p-2 text-xs">
        <ManagedNotice kind="sites" itemKey={view.data.environmentKey} />

        <label className="flex items-start gap-2">
          <Switch
            checked={settings.indexable}
            disabled={locked}
            onCheckedChange={(indexable) => change({ ...settings, indexable })}
          />
          <span className="font-medium">{t('searchEngines.indexable')}</span>
        </label>
        {!settings.indexable && <Callout tone="warning">{t('searchEngines.hiddenSite')}</Callout>}
        {view.data.sitemapUrl && settings.indexable && (
          <a
            className="inline-flex items-center gap-1 text-primary hover:underline"
            href={view.data.sitemapUrl}
            target="_blank"
            rel="noreferrer"
          >
            <ExternalLink className="h-3 w-3" />
            {t('searchEngines.viewSitemap')}
          </a>
        )}

        {view.data.automaticPages && (
        <section aria-label={t('searchEngines.pages')} className={settings.indexable ? undefined : 'opacity-50'}>
          <h3 className="font-semibold">{t('searchEngines.pages')}</h3>
          <p className="mb-1 text-2xs text-muted-foreground">{t('searchEngines.pagesHint')}</p>
          {view.data.pages == null ? (
            <p className="text-muted-foreground">{t('searchEngines.noBuild')}</p>
          ) : (
            <ul className="space-y-1">
              {view.data.pages.map((page) => {
                const rule = hiddenBy(settings, page.path);
                const own = rule?.toLowerCase() === page.path.toLowerCase();
                return (
                  <li key={page.path} className="flex items-center gap-2">
                    <Switch
                      aria-label={page.path}
                      checked={!rule}
                      disabled={locked || !settings.indexable || (!!rule && !own)}
                      onCheckedChange={(shown) => change(setPageShown(settings, page.path, shown))}
                    />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-mono">{page.path}</span>
                      {page.instance && page.contentType && (
                        <span className="block truncate text-2xs text-muted-foreground">
                          {t('searchEngines.eachItem', { source: `${page.instance}/${page.contentType}` })}
                        </span>
                      )}
                      {rule && !own && (
                        <span className="block truncate text-2xs text-muted-foreground">
                          {t('searchEngines.hiddenBy', { rule })}
                        </span>
                      )}
                    </span>
                  </li>
                );
              })}
            </ul>
          )}
        </section>
        )}

        <PathList
          title={t('searchEngines.hidden')}
          hint={t('searchEngines.hiddenHint')}
          paths={settings.exclude}
          patterns
          disabled={locked}
          onChange={(exclude) => change({ ...settings, exclude })}
        />
        <PathList
          title={t(view.data.automaticPages ? 'searchEngines.extra' : 'searchEngines.sitemapUrls')}
          hint={t(view.data.automaticPages ? 'searchEngines.extraHint' : 'searchEngines.sitemapUrlsHint')}
          paths={settings.include}
          patterns={false}
          disabled={locked || !settings.indexable}
          onChange={(include) => change({ ...settings, include })}
        />
      </div>
    </div>
  );
}

function PathList({
  title,
  hint,
  paths,
  patterns,
  disabled,
  onChange,
}: {
  title: string;
  hint: string;
  paths: string[];
  patterns: boolean;
  disabled: boolean;
  onChange: (paths: string[]) => void;
}) {
  const { t } = useTranslation();
  const [value, setValue] = useState('');
  const problem = value ? pathProblem(value.trim(), patterns) : null;

  const add = (e: FormEvent) => {
    e.preventDefault();
    const path = value.trim();
    if (!path || problem) return;
    if (!paths.some((p) => p.toLowerCase() === path.toLowerCase())) onChange([...paths, path]);
    setValue('');
  };

  return (
    <section aria-label={title}>
      <h3 className="font-semibold">{title}</h3>
      <p className="mb-1 text-2xs text-muted-foreground">{hint}</p>
      <ul className="mb-1 space-y-0.5">
        {paths.map((path) => (
          <li key={path} className="flex items-center gap-1">
            <span className="min-w-0 flex-1 truncate font-mono">{path}</span>
            <Button
              size="icon"
              variant="ghost"
              className="h-6 w-6"
              aria-label={t('searchEngines.remove', { path })}
              disabled={disabled}
              onClick={() => onChange(paths.filter((p) => p !== path))}
            >
              <X className="h-3 w-3" />
            </Button>
          </li>
        ))}
      </ul>
      <form className="flex gap-1" onSubmit={add}>
        <Input
          className="h-7 font-mono text-xs"
          value={value}
          placeholder="/path"
          aria-label={title}
          aria-invalid={!!problem}
          disabled={disabled}
          onChange={(e) => setValue(e.target.value)}
        />
        <Button type="submit" size="icon" variant="outline" className="h-7 w-7" aria-label={t('searchEngines.add')} disabled={disabled || !value.trim() || !!problem}>
          <Plus className="h-3.5 w-3.5" />
        </Button>
      </form>
      {problem && <p className="mt-0.5 text-2xs text-destructive">{t(`searchEngines.problem.${problem}`)}</p>}
    </section>
  );
}
