import { useMemo } from 'react';
import { Link, Navigate, useNavigate, useSearch } from '@tanstack/react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ArrowRight,
  Database,
  ExternalLink,
  FileText,
  History,
  PackageX,
  Settings as SettingsIcon,
  type LucideIcon,
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import {
  Callout,
  CenteredSpinner,
  cn,
  EmptyState,
  Page,
  PageHeader,
  Skeleton,
  SubNav,
  SubNavGroup,
  SubNavItemBody,
  subNavItemClass,
  Switch,
  toastApiError,
  SectionHeader,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { Perm, can, useMyPermissions } from '../../lib/permissions';
import { getCurrentTenantSlug } from '../../tenants';
import { iconByName } from '../../app/navApi';
import { ResourceHistory } from '../audit/ResourceHistory';
import { PluginReferenceTabs } from '../marketplace/PluginReferencePage';
import { usePluginReference, type PluginReference } from '../marketplace/api';
import { screenTitle, usePluginUi, type PluginUiInfo } from '../../pluginHost/api';
import { PluginScreenView } from '../../pluginHost/PluginScreenView';
import { CollectionView } from '../content/ContentPage';
import { humanize } from '../content/collectionView';
import { DataSetView } from '../plugins/DataSetView';
import { useDataSets, type DataSet } from '../plugins/dataApi';
import { SchemaSettings } from './SchemaSettings';
import {
  parseInstanceConfig,
  usePluginCatalog,
  usePluginInstances,
  type PluginInstance,
  type PluginManifest,
} from '../plugins/api';

interface AppNavEntry {
  /** The page's path segment(s) after `/apps/{slug}/`. */
  id: string;
  label: string;
  icon: LucideIcon;
}

/**
 * One plugin instance as an app (#337): `/apps/{slug}/{page}`.
 *
 * <p>The frame is the same for every plugin and every member — its name and state, then a
 * sub-navigation with the app's work (the plugin's screens, its content collections) and, under
 * Admin, its configuration (the plugin's settings and admin screens, the platform's data,
 * integration and activity pages). Only the pages the member may open are listed, so the people
 * an app is for see its records and nothing about how it is wired.</p>
 *
 * <p>Instance and screens come from `/admin/plugin-ui`, which any member may read; the plugin
 * management endpoints are only asked for by the pages that need plugins:manage.</p>
 */
export function AppPage({ slug, page, type }: { slug: string; page?: string; type?: string }) {
  const { t, i18n } = useTranslation();
  const ui = usePluginUi();
  const catalog = usePluginCatalog();
  const me = useMyPermissions(true);
  const plugin = ui.data?.find((p) => p.instances.some((i) => i.slug === slug));
  const instance = plugin?.instances.find((i) => i.slug === slug);
  const manifest = catalog.data?.find((m) => m.id === plugin?.pluginId);
  const manages = can(me.data, Perm.PluginsManage);
  const sets = useDataSets(slug, !!instance && manages);

  if (ui.isLoading || catalog.isLoading || me.isLoading) {
    return (
      <Page>
        <Skeleton className="h-10 w-72" />
        <Skeleton className="mt-6 h-40 w-full" />
      </Page>
    );
  }
  if (!plugin || !instance) {
    return (
      <Page>
        <EmptyState icon={PackageX} title={t('pluginPage.notFound')} description={t('pluginPage.notFoundHint', { slug })} />
      </Page>
    );
  }

  const allowed = plugin.screens.filter((s) => s.allowed);
  const entry = (s: PluginUiInfo['screens'][number]): AppNavEntry => ({
    id: s.id,
    label: screenTitle(s, i18n.language),
    icon: iconByName(s.icon),
  });
  const work: AppNavEntry[] = [
    // The instance's own screens first; a plugin-wide one (the Forms inbox) covers this app too.
    ...allowed.filter((s) => s.section !== 'admin' && s.id !== 'settings' && s.scope === 'instance').map(entry),
    ...allowed.filter((s) => s.section !== 'admin' && s.id !== 'settings' && s.scope === 'plugin').map(entry),
    ...(can(me.data, Perm.ContentRead)
      ? (manifest?.contentTypes ?? []).map((ct) => ({ id: `content/${ct.name}`, label: humanize(ct.name), icon: FileText }))
      : []),
  ];
  // Every plugin provides its settings screen; one built before that gets the console's form.
  const ownSettings = plugin.screens.some((s) => s.id === 'settings');
  const admin: AppNavEntry[] = [
    ...allowed.filter((s) => s.id === 'settings').map(entry),
    ...(!ownSettings && manages ? [{ id: 'settings', label: t('pluginPage.tabs.settings'), icon: SettingsIcon }] : []),
    ...allowed.filter((s) => s.section === 'admin' && s.id !== 'settings').map(entry),
    ...(manages && (sets.data?.length ?? 0) > 0 ? [{ id: 'data', label: t('pluginPage.tabs.data'), icon: Database }] : []),
    ...(manages && manifest ? [{ id: 'integration', label: t('pluginPage.tabs.integration'), icon: ExternalLink }] : []),
    ...(can(me.data, Perm.AuditRead) ? [{ id: 'activity', label: t('pluginPage.tabs.activity'), icon: History }] : []),
  ];

  const current = page === 'content' && type ? `content/${type}` : page;
  // Switched off, an app's work has nothing behind it; its settings are where it is switched back on.
  const first = (instance.enabled ? work[0] : undefined) ?? admin[0] ?? work[0];
  if (!current && first) return <Navigate to={`/apps/${slug}/${first.id}` as string} replace />;
  const open = [...work, ...admin].find((e) => e.id === current);

  const link = (e: AppNavEntry) => (
    <li key={e.id}>
      <Link
        to={`/apps/${slug}/${e.id}` as string}
        aria-current={e.id === current ? 'page' : undefined}
        className={subNavItemClass(e.id === current)}
      >
        <SubNavItemBody label={e.label} icon={e.icon} active={e.id === current} />
      </Link>
    </li>
  );

  return (
    <Page className="max-w-7xl">
      <AppHeader plugin={plugin} instance={instance} manifest={manifest} manages={manages} />
      <div className="mt-6 flex flex-col gap-4 lg:flex-row lg:gap-8">
        <SubNav label={instance.name} className="lg:sticky lg:top-6 lg:w-52 lg:self-start">
          {work.length > 0 ? <SubNavGroup>{work.map(link)}</SubNavGroup> : null}
          {admin.length > 0 ? <SubNavGroup label={t('apps.admin')}>{admin.map(link)}</SubNavGroup> : null}
        </SubNav>
        <section className="min-w-0 flex-1" aria-label={open?.label}>
          {!instance.enabled ? (
            // Switched off, the plugin serves nothing to sites, but its data is still the tenant's.
            <Callout tone="warning" className="mb-4">
              {t('apps.disabledNotice')}
            </Callout>
          ) : null}
          {!open ? (
            <EmptyState icon={PackageX} title={t('apps.noPage')} description={t('apps.noPageHint')} />
          ) : !instance.enabled && work.includes(open) ? null : (
            <AppPageBody
              key={open.id}
              id={open.id}
              plugin={plugin}
              slug={slug}
              type={type}
              manifest={manifest}
              sets={sets.data ?? []}
            />
          )}
        </section>
      </div>
    </Page>
  );
}

function AppPageBody({
  id,
  plugin,
  slug,
  type,
  manifest,
  sets,
}: {
  id: string;
  plugin: PluginUiInfo;
  slug: string;
  type?: string;
  manifest: PluginManifest | undefined;
  sets: DataSet[];
}) {
  const instance = plugin.instances.find((i) => i.slug === slug)!;
  if (id === 'settings' && !plugin.screens.some((s) => s.id === 'settings')) return <SchemaSettings slug={slug} />;
  if (!manifest) return <PluginScreenView plugin={plugin} screenId={id} instance={instance} />;
  if (id.startsWith('content/')) return <AppCollection slug={slug} manifest={manifest} type={type!} />;
  // The console's own pages of every app, beside the plugin's screens (AdminScreen.Reserved on the server).
  switch (id) {
    case 'data':
      return <DataPage slug={slug} sets={sets} />;
    case 'integration':
      return <IntegrationPage slug={slug} manifest={manifest} />;
    case 'activity':
      return <ResourceHistory resourceType="plugin_instance" resourceId={instance.id} />;
    default:
      return <PluginScreenView plugin={plugin} screenId={id} instance={instance} />;
  }
}

/** One of the app's content collections: the list Content shows for it. */
function AppCollection({ slug, manifest, type }: { slug: string; manifest: PluginManifest; type: string }) {
  // The full instance carries the config a collection's custom fields are defined in.
  const instances = usePluginInstances();
  const instance = instances.data?.find((i) => i.slug === slug);
  const contentType = manifest.contentTypes.find((ct) => ct.name === type);
  if (!instance || !contentType) return instances.isLoading ? <CenteredSpinner /> : null;
  return <CollectionView instance={instance} contentType={contentType} />;
}

/* ---- header ---- */

/** The app's name, the plugin it is, and — for a plugin manager — the switch that turns it on and off. */
function AppHeader({
  plugin,
  instance,
  manifest,
  manages,
}: {
  plugin: PluginUiInfo;
  instance: PluginUiInfo['instances'][number];
  manifest: PluginManifest | undefined;
  manages: boolean;
}) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const Icon = iconByName(plugin.icon);

  const toggle = useMutation({
    mutationFn: (enabled: boolean) =>
      api.post(`/admin/plugins/instances/${instance.id}/${enabled ? 'enable' : 'disable'}`),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['plugin-instances'] });
      await qc.invalidateQueries({ queryKey: ['plugin-ui'] });
      await qc.invalidateQueries({ queryKey: ['plugin-data'] });
      await qc.invalidateQueries({ queryKey: ['navigation'] });
    },
    // A 409 names the plugins this one needs, or the ones that need it.
    onError: (e) => toastApiError(e, t),
  });

  return (
    <PageHeader
      title={
        <span className="flex items-center gap-3">
          <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
            <Icon className="h-5 w-5" aria-hidden />
          </span>
          <span className="min-w-0 [overflow-wrap:anywhere]">{instance.name}</span>
        </span>
      }
      description={
        <span className="flex flex-wrap items-center gap-x-4 gap-y-1">
          {manages ? (
            <Link
              to={'/marketplace/$pluginId' as string}
              params={{ pluginId: plugin.pluginId } as never}
              className="hover:text-foreground hover:underline"
            >
              {plugin.name}
            </Link>
          ) : (
            <span>{plugin.name}</span>
          )}
          {manifest ? <span>{t('pluginPage.version', { version: manifest.version })}</span> : null}
          <code className="font-mono text-xs">/{instance.slug}</code>
        </span>
      }
      actions={
        manages ? (
          <label className="flex items-center gap-2 text-sm">
            <Switch
              checked={instance.enabled}
              disabled={toggle.isPending}
              onCheckedChange={(v) => toggle.mutate(v)}
              aria-label={t('pluginPage.enabledLabel')}
            />
            <span className={cn('font-medium', instance.enabled ? 'text-success-foreground' : 'text-muted-foreground')}>
              {instance.enabled ? t('plugins.enabled') : t('plugins.disabled')}
            </span>
          </label>
        ) : null
      }
    />
  );
}

/* ---- data ---- */

function DataPage({ slug, sets }: { slug: string; sets: DataSet[] }) {
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as { set?: string };
  return (
    <DataTab
      slug={slug}
      sets={sets}
      current={search.set}
      onSelect={(set) => void navigate({ to: '.', search: { set } as never, replace: true })}
    />
  );
}

/* ---- integration ---- */

/** How the app is wired to the rest of the workspace, what it is, and how to use it from a site or from C#. */
function IntegrationPage({ slug, manifest }: { slug: string; manifest: PluginManifest }) {
  const { t } = useTranslation();
  const instances = usePluginInstances();
  const reference = usePluginReference(manifest.id);
  const instance = instances.data?.find((i) => i.slug === slug);
  if (!instance) return instances.isLoading ? <CenteredSpinner /> : null;
  return (
    <div className="space-y-8">
      {manifest.description ? <p className="max-w-prose text-sm text-muted-foreground">{manifest.description}</p> : null}
      <Wiring instance={instance} manifest={manifest} reference={reference.data} />
      <dl className="grid grid-cols-1 gap-3 rounded-lg border p-4 text-sm sm:grid-cols-2">
        <Fact label={t('pluginPage.facts.plugin')}>
          {manifest.name} <span className="text-muted-foreground">({manifest.id})</span>
        </Fact>
        <Fact label={t('pluginPage.facts.siteApi')}>
          <code className="font-mono text-xs">/api/{instance.slug}</code>
        </Fact>
        <Fact label={t('pluginPage.facts.instances')}>
          {manifest.allowMultipleInstances ? t('plugins.multiInstance') : t('plugins.singleInstance')}
        </Fact>
        <Fact label={t('pluginPage.facts.ai')}>
          {instance.aiToolsEnabled ? t('pluginPage.facts.aiOn') : t('pluginPage.facts.aiOff')}
        </Fact>
        {manifest.permissions.length > 0 ? (
          <Fact label={t('pluginPage.facts.permissions')}>
            <ul className="space-y-0.5">
              {manifest.permissions.map((p) => (
                <li key={p.action}>{p.displayName}</li>
              ))}
            </ul>
          </Fact>
        ) : null}
        {/* Events it handles are in the wiring above; this is what runs on a timer or on demand. */}
        {reference.data && reference.data.jobs.length > 0 ? (
          <Fact label={t('pluginPage.overview.background')}>
            <ul className="space-y-0.5">
              {reference.data.jobs.map((j) => (
                <li key={j.name}>
                  <code className="font-mono text-xs">{j.name}</code>{' '}
                  <span className="text-muted-foreground">
                    {j.intervalMinutes
                      ? t('pluginReference.every', { minutes: j.intervalMinutes })
                      : t('pluginPage.overview.onDemand')}
                  </span>
                </li>
              ))}
            </ul>
          </Fact>
        ) : null}
      </dl>
      <Integration instance={instance} reference={reference.data} />
    </div>
  );
}

function Wiring({
  instance,
  manifest,
  reference,
}: {
  instance: PluginInstance;
  manifest: PluginManifest;
  reference?: PluginReference;
}) {
  const { t } = useTranslation();
  const instances = usePluginInstances();
  const catalog = usePluginCatalog();
  const all = instances.data ?? [];
  const manifests = catalog.data ?? [];
  const config = useMemo(() => parseInstanceConfig(instance.config), [instance.config]);

  const offers = manifest.provides ?? [];
  const uses = (manifest.consumes ?? []).map((c) => {
    const platform = c.contractId.startsWith('dcms.');
    const providerIds = manifests.filter((m) => m.provides?.includes(c.contractId)).map((m) => m.id);
    const candidates = all.filter((i) => i.enabled && providerIds.includes(i.pluginId) && i.id !== instance.id);
    const bound = c.bindingConfigKey ? String(config[c.bindingConfigKey] ?? '') : '';
    const answering = bound ? candidates.filter((i) => i.id === bound || i.slug === bound) : candidates;
    return { ...c, platform, answering };
  });
  // Instances relying on this one: a consumer bound to it, or unbound with this as a provider.
  const usedBy = all.filter((other) => {
    if (other.id === instance.id || !other.enabled) return false;
    const m = manifests.find((x) => x.id === other.pluginId);
    return (m?.consumes ?? []).some((c) => {
      if (!offers.includes(c.contractId)) return false;
      const bound = c.bindingConfigKey ? String(parseInstanceConfig(other.config)[c.bindingConfigKey] ?? '') : '';
      return !bound || bound === instance.id || bound === instance.slug;
    });
  });
  const listens = reference?.subscribes ?? [];

  if (offers.length + uses.length + usedBy.length + listens.length === 0) return null;

  return (
    <dl
      className="mt-5 grid grid-cols-1 gap-x-6 gap-y-3 rounded-lg border border-l-4 border-l-primary/60 bg-muted/30 px-4 py-3 text-sm sm:grid-cols-[7rem_1fr]"
      aria-label={t('pluginPage.wiring.label')}
    >
      {offers.length > 0 ? (
        <WiringRow label={t('pluginPage.wiring.offers')}>
          {offers.map((id) => (
            <Chip key={id} tone="offer">
              {id}
            </Chip>
          ))}
        </WiringRow>
      ) : null}
      {uses.length > 0 ? (
        <WiringRow label={t('pluginPage.wiring.uses')}>
          {uses.map((u) => (
            <span key={u.contractId} className="inline-flex items-center gap-1">
              <Chip tone={u.platform ? 'platform' : 'use'}>{u.contractId}</Chip>
              {u.platform ? (
                <span className="text-xs text-muted-foreground">{t('pluginPage.wiring.platform')}</span>
              ) : u.answering.length > 0 ? (
                <>
                  <ArrowRight className="h-3 w-3 text-muted-foreground" aria-hidden />
                  {u.answering.map((a) => (
                    <InstanceLink key={a.id} instance={a} />
                  ))}
                </>
              ) : (
                <span className={cn('text-xs', u.optional ? 'text-muted-foreground' : 'text-destructive')}>
                  {u.optional ? t('pluginPage.wiring.optionalMissing') : t('pluginPage.wiring.missing')}
                </span>
              )}
            </span>
          ))}
        </WiringRow>
      ) : null}
      {usedBy.length > 0 ? (
        <WiringRow label={t('pluginPage.wiring.usedBy')}>
          {usedBy.map((i) => (
            <InstanceLink key={i.id} instance={i} />
          ))}
        </WiringRow>
      ) : null}
      {listens.length > 0 ? (
        <WiringRow label={t('pluginPage.wiring.listens')}>
          {listens.map((e) => (
            <Chip key={e} tone="platform">
              {e}
            </Chip>
          ))}
        </WiringRow>
      ) : null}
    </dl>
  );
}

function WiringRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt className="pt-0.5 text-xs font-medium text-muted-foreground">{label}</dt>
      <dd className="flex flex-wrap items-center gap-x-3 gap-y-1.5">{children}</dd>
    </>
  );
}

function Chip({ tone, children }: { tone: 'offer' | 'use' | 'platform'; children: React.ReactNode }) {
  return (
    <code
      className={cn(
        'rounded px-1.5 py-0.5 font-mono text-xs',
        tone === 'offer' && 'bg-primary/10 text-primary',
        tone === 'use' && 'border bg-background',
        tone === 'platform' && 'text-muted-foreground',
      )}
    >
      {children}
    </code>
  );
}

function InstanceLink({ instance }: { instance: PluginInstance }) {
  return (
    <Link
      to={'/apps/$slug' as string}
      params={{ slug: instance.slug } as never}
      className="font-medium text-foreground underline-offset-2 hover:underline"
    >
      {instance.name}
    </Link>
  );
}

function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="mt-0.5">{children}</dd>
    </div>
  );
}

function DataTab({
  slug,
  sets,
  current,
  onSelect,
}: {
  slug: string;
  sets: DataSet[];
  current?: string;
  onSelect: (set: string) => void;
}) {
  const set = sets.find((s) => s.id === current) ?? sets[0];
  if (sets.length === 1) return <DataSetView key={set.id} slug={slug} set={set} />;
  return (
    <div className="grid grid-cols-1 gap-6 md:grid-cols-[13rem_minmax(0,1fr)]">
      <nav className="flex gap-1 overflow-x-auto md:flex-col" aria-label="Data sets">
        {sets.map((s) => {
          const Icon = iconByName(s.icon ?? 'Database');
          return (
            <button
              key={s.id}
              type="button"
              onClick={() => onSelect(s.id)}
              aria-current={s.id === set.id ? 'page' : undefined}
              className={cn(
                'flex shrink-0 items-center gap-2 rounded-md px-3 py-2 text-left text-sm transition-colors',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                s.id === set.id ? 'bg-accent font-medium text-accent-foreground' : 'text-muted-foreground hover:bg-muted',
              )}
            >
              <Icon className="h-4 w-4" aria-hidden />
              {s.title}
            </button>
          );
        })}
      </nav>
      {/* Keyed so each set starts with its own filters, sort and page. */}
      <DataSetView key={set.id} slug={slug} set={set} />
    </div>
  );
}

/* ---- site and developer reference ---- */

const METHODS = ['get', 'post', 'put', 'patch', 'delete'] as const;

function Integration({ instance, reference }: { instance: PluginInstance; reference?: PluginReference }) {
  const { t } = useTranslation();
  const tenant = getCurrentTenantSlug();
  const doc = useQuery({
    queryKey: ['openapi', tenant],
    staleTime: 60_000,
    queryFn: () => api.get<{ paths?: Record<string, Record<string, { summary?: string }>> }>('/admin/openapi.json'),
  });
  const prefix = `/api/${instance.slug}`;
  const routes = Object.entries(doc.data?.paths ?? {})
    .filter(([path]) => path === prefix || path.startsWith(`${prefix}/`))
    .flatMap(([path, ops]) =>
      METHODS.filter((m) => ops[m]).map((m) => ({ method: m.toUpperCase(), path, summary: ops[m]?.summary })),
    );

  return (
    <div className="space-y-8">
      <section>
        <div className="mb-3 flex flex-wrap items-baseline justify-between gap-2">
          <h2 className="text-base font-semibold">{t('pluginPage.integration.site')}</h2>
          <Link to={'/settings/api' as string} className="inline-flex items-center gap-1 text-sm text-primary hover:underline">
            {t('pluginPage.integration.fullDocs')}
            <ExternalLink className="h-3.5 w-3.5" aria-hidden />
          </Link>
        </div>
        <p className="mb-3 max-w-prose text-sm text-muted-foreground">
          {instance.enabled ? t('pluginPage.integration.siteHint') : t('pluginPage.integration.siteDisabled')}
        </p>
        {doc.isLoading ? (
          <CenteredSpinner />
        ) : routes.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t('pluginPage.integration.noRoutes')}</p>
        ) : (
          <ul className="divide-y rounded-lg border">
            {routes.map((r) => (
              <li key={`${r.method} ${r.path}`} className="flex flex-wrap items-baseline gap-x-3 gap-y-1 px-4 py-2 text-sm">
                <span className="w-14 shrink-0 font-mono text-xs font-semibold text-primary">{r.method}</span>
                <code className="min-w-0 font-mono text-xs [overflow-wrap:anywhere]">{r.path}</code>
                {r.summary ? <span className="text-muted-foreground">{r.summary}</span> : null}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <SectionHeader
          title={t('pluginPage.integration.developers')}
          description={t('pluginPage.integration.developersHint')}
        />
        {reference ? <PluginReferenceTabs reference={reference} /> : <CenteredSpinner />}
      </section>
    </div>
  );
}

/**
 * `/app/{pluginId}/{screen}`: a plugin-wide screen's own page before every instance was an app.
 * Notification rows and bookmarks still carry it; it opens in the first enabled instance's app.
 */
export function PluginAppRedirect({ pluginId, screenId }: { pluginId: string; screenId: string }) {
  const { t } = useTranslation();
  const ui = usePluginUi();
  const search = useSearch({ strict: false });
  if (ui.isLoading) return <CenteredSpinner />;
  const plugin = ui.data?.find((p) => p.pluginId === pluginId);
  // Switched off everywhere, the first one's app says so, beside the switch.
  const instance = plugin?.instances.find((i) => i.enabled) ?? plugin?.instances[0];
  if (!plugin || !instance) {
    return (
      <Page>
        <EmptyState
          icon={PackageX}
          title={plugin ? t('pluginHost.notInstalled', { plugin: plugin.name }) : t('pluginHost.noPlugin')}
          description={plugin ? t('pluginHost.notInstalledHint') : t('pluginHost.noPluginHint', { plugin: pluginId })}
          action={
            <Link to={'/marketplace' as string} className="text-sm font-medium text-primary hover:underline">
              {t('pluginHost.openMarketplace')}
            </Link>
          }
        />
      </Page>
    );
  }
  return <Navigate to={`/apps/${instance.slug}/${screenId}` as string} search={search as never} replace />;
}
