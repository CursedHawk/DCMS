import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Info } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Button, Card, CardContent, CenteredSpinner, Input, toast, toastApiError } from '@dcms/ui';
import { usePluginApi, usePluginT, type PluginScreenProps } from '@dcms/plugin-ui';

interface Site {
  id: string;
  name: string;
  renderMode: string;
}

interface Instance {
  id: string;
  config: string;
}

/** The same rule the plugin's config schema and its /api/ga/config check apply. */
export const MEASUREMENT_ID = /^G-[A-Z0-9]{4,20}$/;

/** The instance config's `sites` map; anything unreadable is treated as empty, not as an error. */
function parseSites(config: string | undefined): Record<string, string> {
  try {
    const sites = (JSON.parse(config ?? '{}') as { sites?: unknown }).sites;
    return sites && typeof sites === 'object' ? (sites as Record<string, string>) : {};
  } catch {
    return {};
  }
}

/**
 * The plugin's instance screen "sites": one Measurement ID per site of the workspace. Saved into
 * the instance config (`{ sites: { [siteId]: "G-…" } }`) through the ordinary instance update,
 * which validates it against the manifest schema; the site runtimes read it from /api/ga/config.
 */
export function SitesScreen({ instance }: PluginScreenProps) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const qc = useQueryClient();

  const sites = useQuery({ queryKey: ['sites'], queryFn: () => api.get<Site[]>('/admin/sites') });
  const instances = useQuery({
    queryKey: ['plugin-instances'],
    queryFn: () => api.get<Instance[]>('/admin/plugins/instances'),
  });
  const stored = instances.data?.find((i) => i.id === instance!.id);

  const [ids, setIds] = useState<Record<string, string>>({});
  useEffect(() => setIds(parseSites(stored?.config)), [stored?.config]);

  const invalid = Object.values(ids).some((id) => id !== '' && !MEASUREMENT_ID.test(id));

  const save = useMutation({
    mutationFn: () => {
      const config = JSON.parse(stored?.config ?? '{}') as Record<string, unknown>;
      // Empty inputs mean "no GA on this site": dropped rather than stored as "".
      config.sites = Object.fromEntries(Object.entries(ids).filter(([, id]) => id !== ''));
      return api.put(`/admin/plugins/instances/${instance!.id}`, { config: JSON.stringify(config) });
    },
    onSuccess: async () => {
      toast.success(t('ga.saved'));
      await qc.invalidateQueries({ queryKey: ['plugin-instances'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  if (sites.isLoading || instances.isLoading) return <CenteredSpinner />;

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">{t('ga.intro')}</p>
      {instance!.enabled ? null : (
        <p className="rounded-md border border-warning/40 bg-warning/10 p-3 text-sm">{t('ga.disabled')}</p>
      )}

      <Card>
        <CardContent className="divide-y p-0">
          {sites.data?.length ? (
            sites.data.map((site) => {
              const value = ids[site.id] ?? '';
              const bad = value !== '' && !MEASUREMENT_ID.test(value);
              const uploaded = site.renderMode === 'StaticFiles';
              return (
                <div key={site.id} className="flex flex-col gap-2 p-4 sm:flex-row sm:items-center">
                  <label htmlFor={`ga-${site.id}`} className="min-w-0 flex-1 truncate font-medium">
                    {site.name}
                  </label>
                  {uploaded ? (
                    <p className="text-sm text-muted-foreground sm:w-80">{t('ga.uploaded')}</p>
                  ) : (
                    <div className="sm:w-80">
                      <Input
                        id={`ga-${site.id}`}
                        value={value}
                        placeholder={t('ga.placeholder')}
                        aria-label={`${t('ga.measurementId')}: ${site.name}`}
                        aria-invalid={bad}
                        spellCheck={false}
                        autoComplete="off"
                        className="font-mono"
                        onChange={(e) => setIds({ ...ids, [site.id]: e.target.value.trim().toUpperCase() })}
                      />
                      {bad ? <p className="mt-1 text-xs text-destructive">{t('ga.invalid')}</p> : null}
                    </div>
                  )}
                </div>
              );
            })
          ) : (
            <p className="p-4 text-sm text-muted-foreground">{t('ga.noSites')}</p>
          )}
        </CardContent>
      </Card>

      <div className="flex justify-end">
        <Button disabled={invalid || save.isPending || !stored} onClick={() => save.mutate()}>
          {t('ga.save')}
        </Button>
      </div>

      <ul className="space-y-2 text-sm text-muted-foreground">
        {(['public', 'pageViews'] as const).map((key) => (
          <li key={key} className="flex gap-2">
            <Info className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>{t(`ga.${key}`)}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
