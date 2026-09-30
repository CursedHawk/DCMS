import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, ArrowRight, Database, ExternalLink, FileText } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CenteredSpinner,
  cn,
  EmptyState,
  Input,
  Label,
  Page,
  Skeleton,
  Switch,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
  toastApiError,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { Perm, can, useMyPermissions } from '../../lib/permissions';
import { getCurrentTenantSlug } from '../../tenants';
import { iconByName } from '../../app/navApi';
import { SchemaForm } from '../../components/SchemaForm';
import { ResourceHistory } from '../audit/ResourceHistory';
import { PluginReferenceTabs } from '../marketplace/PluginReferencePage';
import { useMarketplace, usePluginReference, type PluginReference } from '../marketplace/api';
import { configWidgets } from './configWidgets';
import { DataSetView } from './DataSetView';
import { useDataPage, useDataSets, type DataSet } from './dataApi';
import {
  parseConfigSchema,
  parseInstanceConfig,
  usePluginCatalog,
  usePluginInstances,
  type PluginInstance,
  type PluginManifest,
} from './api';

const TABS = ['overview', 'data', 'settings', 'integration', 'activity'] as const;
type Tab = (typeof TABS)[number];

/**
 * Everything about one installed plugin instance in one place: what it is and how it is wired
 * to the rest of the workspace, its data (any data set the plugin describes, rendered
 * generically), its configuration, how to use it from a site or from C#, and what has been done
 * to it. Nothing here is written for a particular plugin.
 */
export function PluginInstancePage({ slug }: { slug: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as { tab?: string; set?: string };
  const tab: Tab = (TABS as readonly string[]).includes(search.tab ?? '') ? (search.tab as Tab) : 'overview';
  const go = (next: { tab?: Tab; set?: string }) =>
    void navigate({ to: '/plugins/$slug' as string, params: { slug } as never, search: { ...search, ...next } as never });

  const instances = usePluginInstances();
  const catalog = usePluginCatalog();
  const instance = instances.data?.find((i) => i.slug === slug);
  const manifest = catalog.data?.find((m) => m.id === instance?.pluginId);
  const reference = usePluginReference(instance?.pluginId ?? '');
  const sets = useDataSets(slug, !!instance?.enabled);
  const me = useMyPermissions(true);

  if (instances.isLoading || catalog.isLoading) {
    return (
      <Page>
        <Skeleton className="h-10 w-72" />
        <Skeleton className="mt-6 h-40 w-full" />
      </Page>
    );
  }
  if (!instance || !manifest) {
    return (
      <Page>
        <BackLink />
        <EmptyState title={t('pluginPage.notFound')} description={t('pluginPage.notFoundHint', { slug })} />
      </Page>
    );
  }

  const dataSets = sets.data ?? [];
  return (
    <Page className="max-w-7xl">
      <BackLink />
      <Nameplate instance={instance} manifest={manifest} reference={reference.data} />

      <Tabs value={tab} onValueChange={(v) => go({ tab: v as Tab })} className="mt-6">
        {/* Scrolls on a phone rather than pushing the page sideways. */}
        <TabsList className="max-w-full justify-start overflow-x-auto">
          <TabsTrigger value="overview">{t('pluginPage.tabs.overview')}</TabsTrigger>
          <TabsTrigger value="data">
            {t('pluginPage.tabs.data')}
            {dataSets.length > 0 ? ` (${dataSets.length})` : ''}
          </TabsTrigger>
          <TabsTrigger value="settings">{t('pluginPage.tabs.settings')}</TabsTrigger>
          <TabsTrigger value="integration">{t('pluginPage.tabs.integration')}</TabsTrigger>
          {can(me.data, Perm.AuditRead) ? (
            <TabsTrigger value="activity">{t('pluginPage.tabs.activity')}</TabsTrigger>
          ) : null}
        </TabsList>

        <TabsContent value="overview">
          <Overview
            instance={instance}
            manifest={manifest}
            reference={reference.data}
            dataSets={dataSets}
            onBrowse={(set) => go({ tab: 'data', set })}
          />
        </TabsContent>

        <TabsContent value="data">
          {!instance.enabled ? (
            <EmptyState icon={Database} title={t('pluginPage.data.disabled')} description={t('pluginPage.data.disabledHint')} />
          ) : sets.isLoading ? (
            <CenteredSpinner />
          ) : dataSets.length === 0 ? (
            <EmptyState
              icon={Database}
              title={t('pluginPage.data.none')}
              description={manifest.contentTypes.length > 0 ? t('pluginPage.data.noneContentHint') : t('pluginPage.data.noneHint')}
            />
          ) : (
            <DataTab slug={slug} sets={dataSets} current={search.set} onSelect={(set) => go({ set })} />
          )}
        </TabsContent>

        <TabsContent value="settings">
          <Settings instance={instance} manifest={manifest} />
        </TabsContent>

        <TabsContent value="integration">
          <Integration instance={instance} reference={reference.data} />
        </TabsContent>

        <TabsContent value="activity">
          <ResourceHistory resourceType="plugin_instance" resourceId={instance.id} />
        </TabsContent>
      </Tabs>
    </Page>
  );
}

function BackLink() {
  const { t } = useTranslation();
  return (
    <Link
      to={'/plugins' as string}
      className="mb-4 inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
    >
      <ArrowLeft className="h-4 w-4" aria-hidden />
      {t('plugins.title')}
    </Link>
  );
}

/* ---- header ---- */

/**
 * The instance's name and state, then its wiring: which contracts it offers, which it uses and
 * who answers them, and which instances rely on it. In a plugin system a plugin is mostly its
 * connections, so this is the part of the header worth the space.
 */
function Nameplate({
  instance,
  manifest,
  reference,
}: {
  instance: PluginInstance;
  manifest: PluginManifest;
  reference?: PluginReference;
}) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const market = useMarketplace();
  const item = market.data?.items.find((m) => m.id === manifest.id);
  const Icon = iconByName(item?.icon);

  const toggle = useMutation({
    mutationFn: (enabled: boolean) =>
      api.post(`/admin/plugins/instances/${instance.id}/${enabled ? 'enable' : 'disable'}`),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['plugin-instances'] });
      await qc.invalidateQueries({ queryKey: ['plugin-data'] });
      await qc.invalidateQueries({ queryKey: ['navigation'] });
    },
    // A 409 names the plugins this one needs, or the ones that need it.
    onError: (e) => toastApiError(e, t),
  });

  return (
    <header>
      <div className="flex flex-wrap items-start gap-4">
        <div className="flex h-12 w-12 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
          <Icon className="h-6 w-6" aria-hidden />
        </div>
        <div className="min-w-0 flex-1">
          <h1 className="truncate text-2xl font-bold tracking-tight">{instance.name}</h1>
          <div className="mt-1 flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground">
            <Link
              to={'/marketplace/$pluginId' as string}
              params={{ pluginId: manifest.id } as never}
              className="hover:text-foreground hover:underline"
            >
              {manifest.name}
            </Link>
            <span>{t('pluginPage.version', { version: manifest.version })}</span>
            {item ? <Badge tone="secondary">{t(`pluginPage.source.${item.source}`, { defaultValue: item.source })}</Badge> : null}
            <code className="font-mono text-xs">/{instance.slug}</code>
          </div>
          {instance.description ? <p className="mt-2 max-w-prose text-sm">{instance.description}</p> : null}
        </div>
        <label className="flex items-center gap-2 text-sm">
          <Switch
            checked={instance.enabled}
            disabled={toggle.isPending}
            onCheckedChange={(v) => toggle.mutate(v)}
            aria-label={t('pluginPage.enabledLabel')}
          />
          <span className={cn('font-medium', instance.enabled ? 'text-[hsl(var(--success))]' : 'text-muted-foreground')}>
            {instance.enabled ? t('plugins.enabled') : t('plugins.disabled')}
          </span>
        </label>
      </div>
      <Wiring instance={instance} manifest={manifest} reference={reference} />
    </header>
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
      to={'/plugins/$slug' as string}
      params={{ slug: instance.slug } as never}
      className="font-medium text-foreground underline-offset-2 hover:underline"
    >
      {instance.name}
    </Link>
  );
}

/* ---- overview ---- */

function Overview({
  instance,
  manifest,
  reference,
  dataSets,
  onBrowse,
}: {
  instance: PluginInstance;
  manifest: PluginManifest;
  reference?: PluginReference;
  dataSets: DataSet[];
  onBrowse: (set: string) => void;
}) {
  const { t } = useTranslation();
  return (
    <div className="grid gap-6 lg:grid-cols-[1fr_20rem]">
      <div className="space-y-6">
        {manifest.description ? <p className="max-w-prose text-sm text-muted-foreground">{manifest.description}</p> : null}

        {dataSets.length > 0 ? (
          <section>
            <h2 className="mb-2 text-sm font-semibold">{t('pluginPage.overview.data')}</h2>
            <ul className="divide-y rounded-lg border">
              {dataSets.map((s) => (
                <DataSetSummary key={s.id} slug={instance.slug} set={s} onBrowse={() => onBrowse(s.id)} />
              ))}
            </ul>
          </section>
        ) : null}

        {manifest.contentTypes.length > 0 ? (
          <section>
            <h2 className="mb-2 text-sm font-semibold">{t('pluginPage.overview.content')}</h2>
            <ul className="divide-y rounded-lg border">
              {manifest.contentTypes.map((type) => (
                <li key={type.name} className="flex items-center gap-3 px-4 py-2.5 text-sm">
                  <FileText className="h-4 w-4 text-muted-foreground" aria-hidden />
                  <span className="flex-1 font-mono text-xs">{type.name}</span>
                  <span className="text-xs text-muted-foreground">
                    {t('pluginPage.overview.fields', { count: type.fields.length })}
                  </span>
                  <Link
                    to={'/content' as string}
                    search={{ instance: instance.id, type: type.name } as never}
                    className="text-sm text-primary hover:underline"
                  >
                    {t('pluginPage.overview.openContent')}
                  </Link>
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        {/* Events it handles are in the wiring above; this is what runs on a timer or on demand. */}
        {reference && reference.jobs.length > 0 ? (
          <section>
            <h2 className="mb-2 text-sm font-semibold">{t('pluginPage.overview.background')}</h2>
            <ul className="space-y-1 text-sm">
              {reference.jobs.map((j) => (
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
          </section>
        ) : null}
      </div>

      <aside>
        <dl className="space-y-3 rounded-lg border p-4 text-sm">
          <Fact label={t('pluginPage.facts.plugin')}>
            {manifest.name} <span className="text-muted-foreground">({manifest.id})</span>
          </Fact>
          <Fact label={t('pluginPage.facts.siteApi')}>
            <code className="font-mono text-xs">/api/{instance.slug}</code>
          </Fact>
          <Fact label={t('pluginPage.facts.instances')}>
            {manifest.allowMultipleInstances ? t('plugins.multiInstance') : t('plugins.singleInstance')}
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
          <Fact label={t('pluginPage.facts.ai')}>
            {instance.aiToolsEnabled ? t('pluginPage.facts.aiOn') : t('pluginPage.facts.aiOff')}
          </Fact>
        </dl>
      </aside>
    </div>
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

function DataSetSummary({ slug, set, onBrowse }: { slug: string; set: DataSet; onBrowse: () => void }) {
  const { t } = useTranslation();
  const Icon = iconByName(set.icon ?? 'Database');
  // One row is enough to learn the total.
  const count = useDataPage(slug, set.id, { search: '', sort: null, descending: false, filters: {}, page: 1, pageSize: 1 });
  return (
    <li className="flex items-center gap-3 px-4 py-3">
      <Icon className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{set.title}</p>
        {set.description ? <p className="truncate text-xs text-muted-foreground">{set.description}</p> : null}
      </div>
      <span className="text-sm tabular-nums text-muted-foreground">
        {count.data ? t('pluginPage.overview.rows', { count: count.data.total }) : ''}
      </span>
      <Button size="sm" variant="outline" onClick={onBrowse}>
        {t('pluginPage.overview.browse')}
      </Button>
    </li>
  );
}

/* ---- data ---- */

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
    <div className="grid gap-6 md:grid-cols-[13rem_1fr]">
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

/* ---- settings ---- */

function Settings({ instance, manifest }: { instance: PluginInstance; manifest: PluginManifest }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [name, setName] = useState(instance.name);
  const [description, setDescription] = useState(instance.description ?? '');
  const [aiTools, setAiTools] = useState(instance.aiToolsEnabled ?? false);
  // Seeded from what is stored: the form posts the whole config back, so starting empty would wipe it.
  const [config, setConfig] = useState<unknown>(() => parseInstanceConfig(instance.config));
  useEffect(() => setConfig(parseInstanceConfig(instance.config)), [instance.config]);
  const schema = parseConfigSchema(manifest.configJsonSchema);
  const descriptionRequired = manifest.allowMultipleInstances;
  // Only a plugin that offers contracts has anything an AI agent could use.
  const offersContracts = (manifest.provides?.length ?? 0) > 0;

  const save = useMutation({
    mutationFn: () =>
      api.put(`/admin/plugins/instances/${instance.id}`, {
        name: name.trim(),
        description: description.trim() || undefined,
        config: JSON.stringify(config ?? {}),
        aiToolsEnabled: aiTools,
      }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['plugin-instances'] });
      // Data sets may describe themselves from config (VisitorAuth's attribute columns).
      await qc.invalidateQueries({ queryKey: ['plugin-data'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <div className="max-w-3xl space-y-5">
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label htmlFor="plugin-name">{t('common.name')}</Label>
          <Input id="plugin-name" value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        <div className="space-y-1.5">
          <Label>{t('common.slug')}</Label>
          <Input value={instance.slug} disabled className="font-mono" />
        </div>
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="plugin-description">
          {t('plugins.description')}
          {descriptionRequired ? <span className="ml-1 text-destructive">*</span> : null}
        </Label>
        <Textarea
          id="plugin-description"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          placeholder={t('plugins.descriptionPlaceholder')}
        />
        <p className="text-xs text-muted-foreground">{t('plugins.descriptionHint')}</p>
      </div>
      {offersContracts ? (
        <div className="flex items-start justify-between gap-4 rounded-md border p-3">
          <div className="space-y-0.5">
            <Label htmlFor="plugin-ai-tools">{t('plugins.aiTools.label')}</Label>
            <p className="text-xs text-muted-foreground">{t('plugins.aiTools.hint')}</p>
          </div>
          <Switch id="plugin-ai-tools" checked={aiTools} onCheckedChange={setAiTools} />
        </div>
      ) : null}
      {Object.keys(schema).length > 0 ? (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">{t('plugins.configuration')}</CardTitle>
          </CardHeader>
          <CardContent>
            <SchemaForm
              schema={schema}
              formData={config}
              onChange={setConfig}
              widgets={configWidgets}
              // The Meta widget's Sync-now button needs the instance it belongs to.
              formContext={{ instanceId: instance.id }}
            />
          </CardContent>
        </Card>
      ) : null}
      <div className="flex justify-end">
        <Button disabled={(descriptionRequired && !description.trim()) || save.isPending} onClick={() => save.mutate()}>
          {t('actions.save')}
        </Button>
      </div>
    </div>
  );
}

/* ---- integration ---- */

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
        <div className="mb-2 flex flex-wrap items-baseline justify-between gap-2">
          <h2 className="text-sm font-semibold">{t('pluginPage.integration.site')}</h2>
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
                <code className="font-mono text-xs">{r.path}</code>
                {r.summary ? <span className="text-muted-foreground">{r.summary}</span> : null}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <h2 className="mb-1 text-sm font-semibold">{t('pluginPage.integration.developers')}</h2>
        <p className="mb-3 max-w-prose text-sm text-muted-foreground">{t('pluginPage.integration.developersHint')}</p>
        {reference ? <PluginReferenceTabs reference={reference} /> : <CenteredSpinner />}
      </section>
    </div>
  );
}
