import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Plug, Search, Settings2, Store } from 'lucide-react';
import { useState } from 'react';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Card,
  CardContent,
  CenteredSpinner,
  cn,
  EmptyState,
  Input,
  LoadFailed,
  Page,
  PageHeader,
  Switch,
  toastApiError,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { usePluginCatalog, usePluginInstances } from './api';
import { InstallPluginDialog } from './InstallPluginDialog';
import { ManagedNotice } from '../environments/ManagedNotice';
import { useManaged } from '../environments/api';

export function PluginsPage() {
  const { t } = useTranslation();
  const { managed } = useManaged('plugins');
  const qc = useQueryClient();
  const navigate = useNavigate();
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();
  // `?add=<pluginId>`: an old Marketplace link, or a bookmark of one. Opens the add dialog.
  const search = useSearch({ strict: false }) as { add?: string };
  const adding = search.add ? catalog.data?.find((m) => m.id === search.add) : undefined;
  const [query, setQuery] = useState('');

  const toggle = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      api.post(`/admin/plugins/instances/${id}/${enabled ? 'enable' : 'disable'}`),
    // Switched on or off, an instance enters or leaves the menu.
    onSuccess: async () =>
      Promise.all(['plugin-instances', 'plugin-ui', 'navigation'].map((key) => qc.invalidateQueries({ queryKey: [key] }))),
    // A 409 names the plugins this one needs, or the ones that need it.
    onError: (e) => toastApiError(e, t),
  });

  const manifestById = new Map((catalog.data ?? []).map((m) => [m.id, m]));
  const allInstances = instances.data ?? [];
  const enabledCount = allInstances.filter((i) => i.enabled).length;

  const q = query.trim().toLowerCase();
  const matches = (text: string) => !q || text.toLowerCase().includes(q);
  const filteredInstances = allInstances.filter(
    (i) => matches(i.name) || matches(i.slug) || matches(i.pluginId),
  );

  const browse = (
    <Link
      to={'/marketplace' as string}
      className="inline-flex h-9 items-center gap-2 rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground shadow-sm transition-colors hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      <Store className="h-4 w-4" aria-hidden />
      {t('plugins.browseMarketplace')}
    </Link>
  );

  return (
    <Page>
      <PageHeader title={t('plugins.title')} description={t('plugins.subtitle')} actions={browse} />
      <ManagedNotice kind="plugins" />
      {instances.isLoading ? (
        <CenteredSpinner />
      ) : instances.isError ? (
        <LoadFailed onRetry={() => void instances.refetch()} />
      ) : allInstances.length === 0 ? (
        <EmptyState
          icon={Plug}
          title={t('plugins.noneInstalled')}
          description={t('plugins.noneInstalledHint')}
          action={browse}
        />
      ) : (
        <div className="space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="text-xs text-muted-foreground">
              {t('plugins.enabledSummary', { enabled: enabledCount, total: allInstances.length })}
            </p>
            <div className="relative w-full sm:w-64">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={t('plugins.searchPlaceholder')}
                aria-label={t('plugins.searchPlaceholder')}
                className="pl-8"
              />
            </div>
          </div>
          <div className="space-y-2">
          {filteredInstances.map((inst) => {
              const manifest = manifestById.get(inst.pluginId);
              const typeCount = manifest?.contentTypes.length ?? 0;
              return (
                <Card key={inst.id} className={cn(!inst.enabled && 'opacity-60')}>
                  <CardContent className="flex items-center gap-3 p-4">
                    <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-accent text-accent-foreground">
                      <Plug className="h-4 w-4" />
                    </div>
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2">
                        <Link
                          to={'/apps/$slug' as string}
                          params={{ slug: inst.slug } as never}
                          className="truncate font-medium hover:underline"
                          data-testid="plugin-instance-link"
                        >
                          {inst.name}
                        </Link>
                        <Badge tone={inst.enabled ? 'success' : 'secondary'}>
                          {inst.enabled ? t('plugins.enabled') : t('plugins.disabled')}
                        </Badge>
                      </div>
                      <p className="truncate text-xs text-muted-foreground">
                        /{inst.slug} · {inst.pluginId}
                        {typeCount > 0 ? ` · ${t('plugins.typeCount', { count: typeCount })}` : ''}
                      </p>
                      {inst.description ? (
                        <p className="truncate text-xs text-muted-foreground">{inst.description}</p>
                      ) : null}
                    </div>
                    <Link
                      to={'/apps/$slug' as string}
                      params={{ slug: inst.slug } as never}
                      search={{ tab: 'settings' } as never}
                      title={t('plugins.configuration')}
                      aria-label={t('plugins.configuration')}
                      className="inline-flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    >
                      <Settings2 className="h-4 w-4" />
                    </Link>
                    <Switch
                      disabled={managed(inst.slug)}
                      title={managed(inst.slug) ? t('environments.managedTitle') : undefined}
                      checked={inst.enabled}
                      onCheckedChange={(v) => toggle.mutate({ id: inst.id, enabled: v })}
                    />
                  </CardContent>
                </Card>
              );
            })}
            {filteredInstances.length === 0 ? (
              <p className="py-8 text-center text-sm text-muted-foreground">{t('common.noResults')}</p>
            ) : null}
          </div>
        </div>
      )}

      {adding ? (
        <InstallPluginDialog
          manifest={adding}
          onClose={() => void navigate({ to: '/plugins' as string, replace: true })}
          onDone={(created) => {
            void navigate({ to: '/apps/$slug' as string, params: { slug: created.slug } as never });
          }}
        />
      ) : null}
    </Page>
  );
}
