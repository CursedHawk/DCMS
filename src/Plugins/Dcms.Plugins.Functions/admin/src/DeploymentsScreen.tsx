import { useMemo, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { Badge, Button, Callout, DataTable, LoadFailed, toast, toastApiError, useConfirm, type Column } from '@dcms/ui';
import { useCan, usePluginT, type PluginScreenProps } from '@dcms/plugin-ui';
import { useDeployment, useDeploymentActions, useDeployments, type DeploymentStatus, type DeploymentSummary } from './api';

const TONE: Record<DeploymentStatus, 'default' | 'secondary' | 'success' | 'warning' | 'destructive' | 'outline'> = {
  Building: 'secondary',
  Failed: 'destructive',
  Ready: 'warning',
  Active: 'success',
  Superseded: 'outline',
  RolledBack: 'outline',
};

/**
 * Builds of the release branch. A good build waits as Ready until someone who holds what its
 * functions use deploys it; any earlier one can be made live again (rollback).
 */
export function DeploymentsScreen({ instance }: PluginScreenProps) {
  const slug = instance!.slug;
  const { t, i18n } = usePluginT();
  const mayDeploy = useCan('deploy');
  const confirm = useConfirm();
  const deployments = useDeployments(slug);
  const { activate, rebuild } = useDeploymentActions(slug);
  const [open, setOpen] = useState<string | null>(null);
  const detail = useDeployment(slug, open);

  const go = async (d: DeploymentSummary) => {
    const back = d.status !== 'Ready';
    const ok = await confirm({
      title: t(back ? 'deployments.rollbackTitle' : 'deployments.deployTitle', { number: d.number }),
      description: t(back ? 'deployments.rollbackHint' : 'deployments.deployHint'),
      confirmLabel: t(back ? 'deployments.rollback' : 'deployments.deploy'),
    });
    if (!ok) return;
    activate.mutate(d.id, {
      onSuccess: () => toast.success(t('deployments.live', { number: d.number })),
      onError: (e) => toastApiError(e, t),
    });
  };

  const columns = useMemo<Column<DeploymentSummary>[]>(() => [
    { id: 'number', header: '#', width: '4rem', primary: true, cell: (d) => <span className="font-mono">{d.number}</span> },
    { id: 'status', header: t('deployments.status'), cell: (d) => <Badge tone={TONE[d.status]}>{t(`deploymentStatus.${d.status}`)}</Badge> },
    { id: 'commit', header: t('deployments.commit'), cell: (d) => <span className="font-mono text-xs">{d.commitSha.slice(0, 7)}</span> },
    { id: 'functions', header: t('deployments.functions'), cell: (d) => d.functions.length || '—' },
    { id: 'when', header: t('deployments.when'), cell: (d) => new Date(d.activatedAt ?? d.createdAt).toLocaleString(i18n.language) },
    {
      id: 'actions',
      header: '',
      align: 'right',
      hideOnCard: true,
      cell: (d) => mayDeploy && ['Ready', 'Superseded', 'RolledBack'].includes(d.status) ? (
        <Button size="sm" variant={d.status === 'Ready' ? 'default' : 'outline'} disabled={activate.isPending}
          onClick={(e) => { e.stopPropagation(); void go(d); }}>
          {t(d.status === 'Ready' ? 'deployments.deploy' : 'deployments.rollback')}
        </Button>
      ) : null,
    },
  ], [t, i18n.language, mayDeploy, activate.isPending]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div className="space-y-4 pt-2">
      <div className="flex items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">{t('deployments.hint')}</p>
        {mayDeploy ? (
          <Button variant="outline" disabled={rebuild.isPending}
            onClick={() => rebuild.mutate(undefined, { onSuccess: () => toast.success(t('deployments.rebuilding')), onError: (e) => toastApiError(e, t) })}>
            <RefreshCw className="h-4 w-4" /> {t('deployments.rebuild')}
          </Button>
        ) : null}
      </div>
      <DataTable rows={deployments.data ?? []} columns={columns} rowKey={(d) => d.id} isLoading={deployments.isLoading}
        error={deployments.isError ? <LoadFailed title={t('common.loadFailed')} retryLabel={t('common.retry')} onRetry={() => void deployments.refetch()} /> : undefined}
        empty={t('deployments.empty')} onRowClick={(d) => setOpen(d.id === open ? null : d.id)} />
      {open && detail.data ? (
        <section className="space-y-3 rounded-lg border p-4">
          <h3 className="text-sm font-semibold">{t('deployments.detail', { number: detail.data.deployment.number })}</h3>
          {detail.data.diagnostics?.length ? (
            <Callout tone="destructive" title={t('deployments.problems')}>
              <ul className="space-y-1 font-mono text-xs">
                {detail.data.diagnostics.map((p, i) => (
                  <li key={i}>{p.file ? `${p.file}${p.line ? `:${p.line}` : ''}: ` : ''}{p.message}</li>
                ))}
              </ul>
            </Callout>
          ) : null}
          {detail.data.permissions.length ? (
            <div className="space-y-1">
              <p className="text-sm">{t('deployments.permissions')}</p>
              <div className="flex flex-wrap gap-1">
                {detail.data.permissions.map((p) => <Badge key={p} tone="outline" className="font-mono">{p}</Badge>)}
              </div>
            </div>
          ) : null}
          {detail.data.functions.length ? (
            <p className="text-sm text-muted-foreground">{detail.data.functions.map((f) => f.name).join(', ')}</p>
          ) : null}
        </section>
      ) : null}
    </div>
  );
}
