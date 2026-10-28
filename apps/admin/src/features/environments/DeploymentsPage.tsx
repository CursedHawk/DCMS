import { ArrowDownToLine, GitCommitHorizontal, History, RotateCcw } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  LoadFailed,
  Page,
  PageHeader,
  Textarea,
  toastApiError,
  usePermissions,
} from '@dcms/ui';
import { can, Perm } from '../../lib/permissions';
import { getCurrentEnvironment } from '../../tenants';
import {
  useApplyPending,
  useCutMigration,
  useEnvironments,
  useHistory,
  usePending,
  useRollback,
  useRollbackPlan,
  useUnpublished,
  type ConfigChange,
  type Environment,
  type Migration,
  type Resolutions,
} from './api';

/**
 * Deployments (ADR 0026): this environment's configuration changes since its last migration and
 * the migration to cut from them; what each environment that deploys here has pending, every
 * change shown against what this environment holds now, conflicts decided before anything runs;
 * and the history, whose latest migration rolls back — reviewed the same way.
 */
export function DeploymentsPage() {
  const { t } = useTranslation();
  const me = usePermissions();
  const environments = useEnvironments();
  const history = useHistory();
  const current = getCurrentEnvironment() ?? 'production';
  const all = environments.data?.environments ?? [];
  const here = all.find((e) => e.name === current);
  const sources = all.filter((e) => here?.promotesFrom.includes(e.id));

  if (environments.isLoading) return <CenteredSpinner />;
  if (environments.isError) return <LoadFailed onRetry={() => void environments.refetch()} />;

  return (
    <Page>
      <PageHeader
        title={t('deployments.title')}
        description={here ? t('deployments.description', { name: here.name }) : undefined}
      />
      {!environments.data?.enabled ? (
        <EmptyState icon={GitCommitHorizontal} title={t('deployments.off')} description={t('environments.offHint')} />
      ) : (
        <div className="flex flex-col gap-8">
          {can(me, Perm.EnvironmentsManage) ? <Unpublished /> : null}
          {can(me, Perm.EnvironmentsPromote)
            ? sources.map((source) => <PendingFrom key={source.id} source={source} />)
            : null}
          <section className="flex flex-col gap-3">
            <h2 className="flex items-center gap-2 text-base font-semibold">
              <History className="h-4 w-4" aria-hidden /> {t('deployments.history')}
            </h2>
            {history.isError ? <LoadFailed onRetry={() => void history.refetch()} /> : null}
            {history.data?.length === 0 ? <p className="text-sm text-muted-foreground">{t('deployments.noHistory')}</p> : null}
            {(history.data ?? []).map((m, i) => (
              <HistoryRow
                key={m.migrationId + m.appliedAt}
                migration={m}
                latest={i === (history.data ?? []).findIndex((x) => !x.rolledBackAt) && !m.rolledBackAt}
                canRollBack={can(me, Perm.EnvironmentsPromote)}
              />
            ))}
          </section>
        </div>
      )}
    </Page>
  );
}

function Unpublished() {
  const { t } = useTranslation();
  const changes = useUnpublished(true);
  const cut = useCutMigration();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const list = changes.data ?? [];

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-base font-semibold">{t('deployments.unpublished')}</h2>
      {changes.isLoading ? <CenteredSpinner /> : null}
      {changes.isError ? <LoadFailed onRetry={() => void changes.refetch()} /> : null}
      {changes.data && list.length === 0 ? <p className="text-sm text-muted-foreground">{t('deployments.nothingChanged')}</p> : null}
      {list.length > 0 ? (
        <Card>
          <CardContent className="flex flex-col gap-2 pt-6">
            {list.map((c) => (
              <ChangeRow key={c.id} change={c} />
            ))}
            <form
              className="mt-2 flex flex-col gap-2"
              onSubmit={(e) => {
                e.preventDefault();
                cut.mutate(
                  { name: name.trim(), description: description.trim() || undefined },
                  {
                    onSuccess: () => {
                      setName('');
                      setDescription('');
                      toast.success(t('deployments.cut'));
                    },
                    onError: (err) => toastApiError(err, t),
                  },
                );
              }}
            >
              <Label htmlFor="migration-name">{t('deployments.migrationName')}</Label>
              <Input id="migration-name" value={name} maxLength={200} onChange={(e) => setName(e.target.value)} />
              <Label htmlFor="migration-description">{t('deployments.migrationDescription')}</Label>
              <Textarea id="migration-description" value={description} rows={2} onChange={(e) => setDescription(e.target.value)} />
              <div>
                <Button type="submit" disabled={!name.trim() || cut.isPending}>
                  <GitCommitHorizontal className="h-4 w-4" /> {t('deployments.createMigration', { count: list.length })}
                </Button>
              </div>
            </form>
          </CardContent>
        </Card>
      ) : null}
    </section>
  );
}

function PendingFrom({ source }: { source: Environment }) {
  const { t } = useTranslation();
  const pending = usePending(source.id, true);
  const apply = useApplyPending();
  const [resolutions, setResolutions] = useState<Resolutions>({});
  const list = pending.data ?? [];

  const deploy = (upTo: string) =>
    apply.mutate(
      { from: source.id, upTo, resolutions },
      { onSuccess: () => toast.success(t('deployments.queued')), onError: (err) => toastApiError(err, t) },
    );

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-base font-semibold">{t('deployments.pendingFrom', { name: source.name })}</h2>
      {pending.isLoading ? <CenteredSpinner /> : null}
      {pending.isError ? <LoadFailed onRetry={() => void pending.refetch()} /> : null}
      {pending.data && list.length === 0 ? <p className="text-sm text-muted-foreground">{t('deployments.upToDate')}</p> : null}
      {list.map(({ migration, changes }, index) => {
        // Applying up to here applies everything before it too, so every conflict up to here counts.
        const unresolved = list
          .slice(0, index + 1)
          .flatMap((p) => p.changes.map((c) => ({ c, key: `${p.migration.migrationId}/${c.id}` })))
          .some(({ c, key }) => c.outcome === 'conflict' && !resolutions[key]);
        return (
          <Card key={migration.migrationId}>
            <CardContent className="flex flex-col gap-2 pt-6">
              <MigrationHeading migration={migration} />
              {changes.map((c) => {
                // One item can change in several pending migrations; each is decided on its own.
                const key = `${migration.migrationId}/${c.id}`;
                return (
                  <ChangeRow
                    key={c.id}
                    change={c}
                    resolution={resolutions[key]}
                    onResolve={(choice) => setResolutions((r) => ({ ...r, [key]: choice }))}
                  />
                );
              })}
              <div className="mt-2 flex flex-wrap items-center gap-3">
                <Button onClick={() => deploy(migration.migrationId)} disabled={unresolved || apply.isPending}>
                  <ArrowDownToLine className="h-4 w-4" />
                  {index === 0 ? t('deployments.deploy') : t('deployments.deployUpTo', { count: index + 1 })}
                </Button>
                {unresolved ? <span className="text-xs text-muted-foreground">{t('deployments.resolveFirst')}</span> : null}
              </div>
            </CardContent>
          </Card>
        );
      })}
    </section>
  );
}

function HistoryRow({ migration, latest, canRollBack }: { migration: Migration; latest: boolean; canRollBack: boolean }) {
  const { t, i18n } = useTranslation();
  const [planFor, setPlanFor] = useState<string | null>(null);
  return (
    <Card>
      <CardContent className="flex flex-col gap-2 pt-6">
        <div className="flex flex-wrap items-start gap-2">
          <MigrationHeading migration={migration} />
          {migration.rolledBackAt ? <Badge tone="warning">{t('deployments.rolledBack')}</Badge> : null}
          {latest && canRollBack ? (
            <Button variant="outline" size="sm" className="ml-auto" onClick={() => setPlanFor(migration.migrationId)}>
              <RotateCcw className="h-4 w-4" /> {t('deployments.rollBack')}
            </Button>
          ) : null}
        </div>
        <p className="text-xs text-muted-foreground">
          {t('deployments.appliedAt', { when: new Date(migration.appliedAt).toLocaleString(i18n.language) })}
        </p>
        <details>
          <summary className="cursor-pointer text-sm">{t('deployments.changes', { count: migration.changes.length })}</summary>
          <div className="mt-2 flex flex-col gap-2">
            {migration.changes.map((c) => (
              <ChangeRow key={c.id} change={c} />
            ))}
          </div>
        </details>
      </CardContent>
      {planFor ? <RollbackDialog migrationId={planFor} onClose={() => setPlanFor(null)} /> : null}
    </Card>
  );
}

function RollbackDialog({ migrationId, onClose }: { migrationId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const plan = useRollbackPlan(migrationId);
  const rollback = useRollback();
  const [resolutions, setResolutions] = useState<Resolutions>({});
  const changes = plan.data?.changes ?? [];
  const unresolved = changes.some((c) => c.outcome === 'conflict' && !resolutions[`${migrationId}/${c.id}`]);
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{t('deployments.rollBackTitle', { name: plan.data?.migration.name ?? '' })}</DialogTitle>
        </DialogHeader>
        {plan.isLoading ? <CenteredSpinner /> : null}
        {plan.isError ? <LoadFailed onRetry={() => void plan.refetch()} /> : null}
        <div className="flex flex-col gap-2">
          {changes.map((c) => (
            <ChangeRow
              key={c.id}
              change={c}
              resolution={resolutions[`${migrationId}/${c.id}`]}
              onResolve={(choice) => setResolutions((r) => ({ ...r, [`${migrationId}/${c.id}`]: choice }))}
            />
          ))}
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button
            variant="destructive"
            disabled={!plan.data || unresolved || rollback.isPending}
            onClick={() =>
              rollback.mutate(
                { migrationId, resolutions },
                {
                  onSuccess: () => {
                    toast.success(t('deployments.rollbackQueued'));
                    onClose();
                  },
                  onError: (err) => toastApiError(err, t),
                },
              )
            }
          >
            {t('deployments.rollBack')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function MigrationHeading({ migration }: { migration: Migration }) {
  const { t } = useTranslation();
  return (
    <div className="min-w-0">
      <h3 className="truncate font-medium">{migration.name}</h3>
      <p className="text-xs text-muted-foreground">
        {[migration.originName && t('deployments.from', { name: migration.originName }), migration.authorEmail]
          .filter(Boolean)
          .join(' · ')}
      </p>
      {migration.description ? <p className="mt-1 text-sm">{migration.description}</p> : null}
    </div>
  );
}

const OUTCOME_TONE = { apply: 'default', already: 'secondary', conflict: 'destructive', local: 'outline' } as const;

/** One change: what it is, how it lands here, and its before and after side by side. */
function ChangeRow({
  change,
  resolution,
  onResolve,
}: {
  change: ConfigChange;
  resolution?: 'overwrite' | 'skip';
  onResolve?: (choice: 'overwrite' | 'skip') => void;
}) {
  const { t } = useTranslation();
  const conflict = change.outcome === 'conflict';
  return (
    <details className="rounded-md border px-3 py-2" open={conflict}>
      <summary className="flex cursor-pointer flex-wrap items-center gap-2 text-sm">
        <Badge tone="outline">{t(`deployments.kinds.${change.kind}`, { defaultValue: change.kind })}</Badge>
        <span className="min-w-0 truncate font-medium">{change.label}</span>
        <span className="text-xs text-muted-foreground">{t(`deployments.ops.${change.op}`)}</span>
        {change.outcome ? (
          <Badge tone={OUTCOME_TONE[change.outcome]} className="ml-auto">
            {t(`deployments.outcomes.${change.outcome}`)}
          </Badge>
        ) : null}
      </summary>
      <div className="mt-2 grid grid-cols-1 gap-2 md:grid-cols-2">
        <Json label={t('deployments.before')} value={change.before} />
        <Json label={t('deployments.after')} value={change.after} />
        {conflict ? <Json label={t('deployments.current')} value={change.current} /> : null}
      </div>
      {conflict && onResolve ? (
        <fieldset className="mt-2 flex flex-wrap gap-4 text-sm">
          <legend className="sr-only">{t('deployments.resolve')}</legend>
          {(['overwrite', 'skip'] as const).map((choice) => (
            <label key={choice} className="flex items-center gap-2">
              <input
                type="radio"
                name={`resolve-${change.id}`}
                checked={resolution === choice}
                onChange={() => onResolve(choice)}
              />
              {t(`deployments.resolutions.${choice}`)}
            </label>
          ))}
        </fieldset>
      ) : null}
    </details>
  );
}

function Json({ label, value }: { label: string; value: unknown }) {
  return (
    <div className="min-w-0">
      <p className="mb-1 text-xs font-medium text-muted-foreground">{label}</p>
      <pre className="max-h-64 overflow-auto rounded bg-muted p-2 text-xs">
        {value == null ? '—' : JSON.stringify(value, null, 2)}
      </pre>
    </div>
  );
}
