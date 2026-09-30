import { useState } from 'react';
import { Link } from '@tanstack/react-router';
import { useMutation } from '@tanstack/react-query';
import { ArrowLeft, Play } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CopyButton,
  EmptyState,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Skeleton,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  toastApiError,
} from '@dcms/ui';
import {
  runContractOperation,
  useAdminContractCatalog,
  usePluginReference,
  type AdminCatalogContract,
  type ReferenceContract,
  type ReferenceOperation,
} from './api';

/**
 * A plugin described for someone building against it: its contracts (with schemas), events,
 * hooks, data and configuration, the packages to reference and C# to start from. Built by the
 * server from the running registry, so it is what the plugin actually does. Operations exposed
 * to the admin can be run here against an instance, through the same dispatcher (and the same
 * permission checks) the AI assistant uses.
 */
export function PluginReferencePage({ pluginId }: { pluginId: string }) {
  const { t } = useTranslation();
  const reference = usePluginReference(pluginId);
  const catalog = useAdminContractCatalog();

  if (reference.isLoading) {
    return (
      <Page>
        <Skeleton className="h-8 w-64" />
        <Skeleton className="mt-6 h-64 w-full" />
      </Page>
    );
  }
  const r = reference.data;
  if (!r) {
    return (
      <Page>
        <EmptyState title={t('pluginReference.notFound')} />
      </Page>
    );
  }

  const events = r.provides.flatMap((c) => c.events.map((e) => ({ ...e, contract: c.id })));
  const hooks = r.provides.flatMap((c) => c.hooks.map((h) => ({ ...h, contract: c.id })));

  return (
    <Page>
      <Link
        to={'/marketplace' as string}
        className="mb-3 inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft className="h-4 w-4" aria-hidden />
        {t('pluginReference.back')}
      </Link>
      <PageHeader
        title={t('pluginReference.title', { name: r.name })}
        description={r.description}
        actions={
          <>
            <Badge tone="secondary">{r.source}</Badge>
            <Badge tone="secondary">v{r.version}</Badge>
          </>
        }
      />

      <Tabs defaultValue="overview">
        <TabsList>
          <TabsTrigger value="overview">{t('pluginReference.tabs.overview')}</TabsTrigger>
          <TabsTrigger value="contracts">
            {t('pluginReference.tabs.contracts')} ({r.provides.length})
          </TabsTrigger>
          <TabsTrigger value="events">
            {t('pluginReference.tabs.events')} ({events.length + hooks.length})
          </TabsTrigger>
          <TabsTrigger value="data">{t('pluginReference.tabs.data')}</TabsTrigger>
          <TabsTrigger value="config">{t('pluginReference.tabs.config')}</TabsTrigger>
        </TabsList>

        <TabsContent value="overview" className="space-y-4">
          <Card>
            <CardHeader>
              <CardTitle>{t('pluginReference.packages')}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-2 text-sm">
              {r.packages.map((p) => (
                <div key={p.id} className="flex flex-wrap items-baseline gap-2">
                  <code className="rounded bg-muted px-1.5 py-0.5">{p.id}</code>
                  <span className="text-muted-foreground">{p.purpose}</span>
                </div>
              ))}
              <p className="text-muted-foreground">{t('pluginReference.sdkMajor', { major: r.sdkMajor })}</p>
            </CardContent>
          </Card>
          <Card>
            <CardHeader className="flex flex-row items-center justify-between space-y-0">
              <CardTitle>{t('pluginReference.csharp')}</CardTitle>
              <CopyButton value={r.cSharp} />
            </CardHeader>
            <CardContent>
              <pre className="overflow-x-auto rounded bg-muted p-3 text-xs" data-testid="plugin-reference-csharp">
                {r.cSharp}
              </pre>
            </CardContent>
          </Card>
          {r.consumes.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>{t('pluginReference.uses')}</CardTitle>
              </CardHeader>
              <CardContent className="space-y-1 text-sm">
                {r.consumes.map((c) => (
                  <div key={c.contractId} className="flex flex-wrap items-baseline gap-2">
                    <code>{c.contractId}</code>
                    {c.optional ? <Badge tone="secondary">{t('marketplace.optional')}</Badge> : null}
                    {c.providers.length > 0 ? (
                      <span className="text-muted-foreground">
                        {t('pluginReference.providedBy', { plugins: c.providers.join(', ') })}
                      </span>
                    ) : (
                      <span className="text-muted-foreground">{t('pluginReference.platform')}</span>
                    )}
                  </div>
                ))}
              </CardContent>
            </Card>
          ) : null}
        </TabsContent>

        <TabsContent value="contracts" className="space-y-4">
          {r.provides.length === 0 ? (
            <EmptyState title={t('pluginReference.noContracts')} />
          ) : (
            r.provides.map((contract) => (
              <ContractCard
                key={contract.id}
                contract={contract}
                catalog={catalog.data?.find((c) => c.id === contract.id)}
              />
            ))
          )}
        </TabsContent>

        <TabsContent value="events" className="space-y-4">
          {events.length + hooks.length === 0 ? (
            <EmptyState title={t('pluginReference.noEvents')} />
          ) : null}
          {events.map((e) => (
            <SchemaCard key={e.name} title={e.name} subtitle={`${e.clrType} · ${e.contract}`} schema={e.schema} badge={t('pluginReference.event')} />
          ))}
          {hooks.map((h) => (
            <SchemaCard key={h.name} title={h.name} subtitle={`${h.clrType} · ${h.contract}`} schema={h.schema} badge={t('pluginReference.hook')} />
          ))}
          {r.subscribes.length > 0 || r.intercepts.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>{t('pluginReference.listensTo')}</CardTitle>
              </CardHeader>
              <CardContent className="space-y-1 text-sm">
                {r.subscribes.map((e) => (
                  <div key={e}>
                    <Badge tone="secondary">{t('pluginReference.event')}</Badge> <code>{e}</code>
                  </div>
                ))}
                {r.intercepts.map((h) => (
                  <div key={h.hook}>
                    <Badge tone="secondary">{t('pluginReference.hook')}</Badge> <code>{h.hook}</code>{' '}
                    <span className="text-muted-foreground">{t('pluginReference.priority', { priority: h.priority })}</span>
                  </div>
                ))}
              </CardContent>
            </Card>
          ) : null}
        </TabsContent>

        <TabsContent value="data" className="space-y-4">
          {r.contentTypes.length === 0 ? <EmptyState title={t('pluginReference.noContentTypes')} /> : null}
          {r.contentTypes.map((type) => (
            <Card key={type.name}>
              <CardHeader>
                <CardTitle className="font-mono text-base">{type.name}</CardTitle>
              </CardHeader>
              <CardContent className="space-y-2 text-sm">
                <table className="w-full text-left">
                  <tbody>
                    {type.fields.map((f) => (
                      <tr key={f.name} className="border-b last:border-0">
                        <td className="py-1 pr-3 font-mono">{f.name}{f.required ? ' *' : ''}</td>
                        <td className="py-1 pr-3 text-muted-foreground">{f.type}</td>
                        <td className="py-1 text-muted-foreground">{f.description}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {type.publishedEvent ? (
                  <p className="text-muted-foreground">
                    {t('pluginReference.lifecycle', { published: type.publishedEvent, unpublished: type.unpublishedEvent ?? '—' })}
                  </p>
                ) : null}
              </CardContent>
            </Card>
          ))}
          {r.jobs.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>{t('pluginReference.jobs')}</CardTitle>
              </CardHeader>
              <CardContent className="space-y-1 text-sm">
                {r.jobs.map((j) => (
                  <div key={j.name}>
                    <code>{j.name}</code>{' '}
                    {j.intervalMinutes ? (
                      <span className="text-muted-foreground">{t('pluginReference.every', { minutes: j.intervalMinutes })}</span>
                    ) : null}
                  </div>
                ))}
              </CardContent>
            </Card>
          ) : null}
        </TabsContent>

        <TabsContent value="config" className="space-y-4">
          <SchemaCard title={t('pluginReference.configSchema')} schema={r.config} />
          {r.permissions.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>{t('marketplace.permissionsTitle')}</CardTitle>
              </CardHeader>
              <CardContent className="space-y-1 text-sm">
                {r.permissions.map((p) => (
                  <div key={p.key}>
                    {p.displayName} <code className="text-muted-foreground">{p.key}</code>
                  </div>
                ))}
              </CardContent>
            </Card>
          ) : null}
        </TabsContent>
      </Tabs>
    </Page>
  );
}

function ContractCard({ contract, catalog }: { contract: ReferenceContract; catalog?: AdminCatalogContract }) {
  const { t } = useTranslation();
  return (
    <Card data-testid="plugin-reference-contract">
      <CardHeader>
        <CardTitle className="font-mono text-base">{contract.id}</CardTitle>
        <p className="text-sm text-muted-foreground">
          {contract.description} <code className="text-xs">{contract.clrType}</code>
          {contract.providers.length > 1 ? ` · ${t('pluginReference.alsoProvidedBy', { plugins: contract.providers.join(', ') })}` : ''}
        </p>
      </CardHeader>
      <CardContent className="space-y-3">
        {contract.operations.map((op) => (
          <OperationRow key={op.name} contractId={contract.id} op={op} catalog={catalog} />
        ))}
      </CardContent>
    </Card>
  );
}

function OperationRow({
  contractId,
  op,
  catalog,
}: {
  contractId: string;
  op: ReferenceOperation;
  catalog?: AdminCatalogContract;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  // Runnable when the admin plane serves it to this member for some instance.
  const runnable = catalog && catalog.operations.some((o) => o.name === op.name) ? catalog.instances : [];
  return (
    <div className="rounded border p-3 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <code className="font-medium">{op.method}({op.inputType ?? ''})</code>
        <span className="text-muted-foreground">→ {op.outputType}</span>
        <Badge tone={op.risk === 'read' ? 'secondary' : 'default'}>{op.risk}</Badge>
        {op.exposed.map((p) => (
          <Badge key={p} tone="secondary">{p}</Badge>
        ))}
        {op.permission ? <code className="text-xs text-muted-foreground">{op.permission}</code> : null}
        <Button variant="ghost" size="sm" className="ml-auto" onClick={() => setOpen((v) => !v)}>
          {open ? t('pluginReference.hide') : t('pluginReference.details')}
        </Button>
      </div>
      {op.description ? <p className="mt-1 text-muted-foreground">{op.description}</p> : null}
      {open ? (
        <div className="mt-3 grid gap-3 md:grid-cols-2">
          <SchemaBlock label={t('pluginReference.input')} schema={op.inputSchema} />
          <SchemaBlock label={t('pluginReference.output')} schema={op.outputSchema} />
          {runnable.length > 0 ? (
            <RunPanel contractId={contractId} op={op} instances={runnable} />
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function RunPanel({
  contractId,
  op,
  instances,
}: {
  contractId: string;
  op: ReferenceOperation;
  instances: AdminCatalogContract['instances'];
}) {
  const { t } = useTranslation();
  const [instance, setInstance] = useState(instances[0]?.slug ?? '');
  const [input, setInput] = useState('{}');
  const run = useMutation({
    mutationFn: () => runContractOperation(contractId, op.name, instance, JSON.parse(input) as unknown),
    onError: (e) => toastApiError(e, t),
  });
  const valid = (() => {
    try {
      JSON.parse(input);
      return true;
    } catch {
      return false;
    }
  })();

  return (
    <div className="space-y-2 md:col-span-2" data-testid="plugin-reference-run">
      <div className="flex flex-wrap items-center gap-2">
        <Select value={instance} onValueChange={setInstance}>
          <SelectTrigger className="w-56" aria-label={t('pluginReference.instance')}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {instances.map((i) => (
              <SelectItem key={i.id} value={i.slug}>
                {i.name} ({i.slug})
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button size="sm" disabled={!valid || run.isPending} onClick={() => run.mutate()}>
          <Play className="h-3.5 w-3.5" aria-hidden />
          {op.risk === 'read' ? t('pluginReference.run') : t('pluginReference.runWrite')}
        </Button>
      </div>
      {op.inputType ? (
        <textarea
          className="h-24 w-full rounded border bg-background p-2 font-mono text-xs"
          value={input}
          aria-label={t('pluginReference.input')}
          onChange={(e) => setInput(e.target.value)}
        />
      ) : null}
      {run.isSuccess ? (
        <pre className="max-h-80 overflow-auto rounded bg-muted p-2 text-xs">
          {JSON.stringify(run.data ?? null, null, 2)}
        </pre>
      ) : null}
    </div>
  );
}

function SchemaBlock({ label, schema }: { label: string; schema: unknown }) {
  return (
    <div>
      <div className="mb-1 text-xs font-medium text-muted-foreground">{label}</div>
      <pre className="max-h-64 overflow-auto rounded bg-muted p-2 text-xs">
        {schema ? JSON.stringify(schema, null, 2) : '—'}
      </pre>
    </div>
  );
}

function SchemaCard({ title, subtitle, schema, badge }: { title: string; subtitle?: string; schema: unknown; badge?: string }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 font-mono text-base">
          {badge ? <Badge tone="secondary">{badge}</Badge> : null}
          {title}
        </CardTitle>
        {subtitle ? <p className="text-sm text-muted-foreground"><code>{subtitle}</code></p> : null}
      </CardHeader>
      <CardContent>
        <pre className="max-h-80 overflow-auto rounded bg-muted p-3 text-xs">{JSON.stringify(schema, null, 2)}</pre>
      </CardContent>
    </Card>
  );
}
