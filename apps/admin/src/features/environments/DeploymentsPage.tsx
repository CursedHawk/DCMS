import { ArrowDownToLine, ArrowRight, GitCommitHorizontal, RotateCcw } from 'lucide-react';
import { useQueries } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  Checkbox,
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
  SectionHeader,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
  toastApiError,
  usePermissions,
} from '@dcms/ui';
import { can, Perm } from '../../lib/permissions';
import { useTabParam } from '../../lib/useTabParam';
import { getCurrentEnvironment } from '../../tenants';
import {
  useApplyPending,
  useCutMigration,
  useEnvironments,
  useHistory,
  usePending,
  pendingQuery,
  useRollback,
  useRollbackPlan,
  useUnpublished,
  type ConfigChange,
  type Environment,
  type Migration,
  type Resolutions,
  isResolved,
} from './api';

/**
 * Deployments (ADR 0026): this environment's configuration changes since its last migration and
 * the migration to cut from them; what each environment that deploys here has pending, every
 * change shown against what this environment holds now, conflicts decided before anything runs;
 * and the history, whose latest migration rolls back — reviewed the same way.
 */
const TABS = ['incoming', 'changes', 'history'] as const;

export function DeploymentsPage() {
  const { t } = useTranslation();
  const me = usePermissions();
  const environments = useEnvironments();
  const history = useHistory();
  const current = getCurrentEnvironment() ?? 'production';
  const all = environments.data?.environments ?? [];
  const here = all.find((e) => e.name === current);
  const sources = all.filter((e) => here?.promotesFrom.includes(e.id));
  const targets = all.filter((e) => here && e.promotesFrom.includes(here.id));
  const canPromote = can(me, Perm.EnvironmentsPromote);
  const canCut = can(me, Perm.EnvironmentsManage);
  const unpublished = useUnpublished(canCut && !!environments.data?.enabled);
  const [tab, setTab] = useTabParam('tab', TABS, 'incoming');
  const pendings = useQueries({ queries: (canPromote ? sources : []).map((s) => pendingQuery(s.id)) });
  const waiting = pendings.reduce((n, q) => n + (q.data?.length ?? 0), 0);

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
        <div className="flex flex-col gap-4">
          {/* Where this environment sits: what deploys in, and where its migrations go next. */}
          <div className="flex flex-wrap items-center gap-2 text-sm">
            {sources.length > 0 ? (
              <>
                <span className="text-muted-foreground">{sources.map((s) => s.name).join(', ')}</span>
                <ArrowRight className="h-4 w-4 text-muted-foreground" aria-hidden />
              </>
            ) : null}
            <Badge tone={here?.isProduction ? 'destructive' : 'secondary'}>{here?.name}</Badge>
            {targets.length > 0 ? (
              <>
                <ArrowRight className="h-4 w-4 text-muted-foreground" aria-hidden />
                <span className="text-muted-foreground">{targets.map((s) => s.name).join(', ')}</span>
              </>
            ) : null}
          </div>
          <Tabs value={tab} onValueChange={setTab}>
            <TabsList>
              <TabsTrigger value="incoming">
                {t('deployments.tabs.incoming')}
                {waiting > 0 ? (
                  <Badge tone="secondary" className="ml-1.5">
                    {waiting}
                  </Badge>
                ) : null}
              </TabsTrigger>
              {canCut ? (
                <TabsTrigger value="changes">
                  {t('deployments.tabs.changes')}
                  {unpublished.data && unpublished.data.length > 0 ? (
                    <Badge tone="secondary" className="ml-1.5">
                      {unpublished.data.length}
                    </Badge>
                  ) : null}
                </TabsTrigger>
              ) : null}
              <TabsTrigger value="history">{t('deployments.tabs.history')}</TabsTrigger>
            </TabsList>
            <TabsContent value="incoming" className="flex flex-col gap-8">
              {!canPromote ? <p className="text-sm text-muted-foreground">{t('deployments.cannotDeploy')}</p> : null}
              {canPromote && sources.length === 0 ? (
                <p className="text-sm text-muted-foreground">{t('deployments.noSources', { name: here?.name ?? '' })}</p>
              ) : null}
              {canPromote ? sources.map((source) => <PendingFrom key={source.id} source={source} />) : null}
            </TabsContent>
            {canCut ? (
              <TabsContent value="changes">
                <Unpublished targets={targets} />
              </TabsContent>
            ) : null}
            <TabsContent value="history" className="flex flex-col gap-3">
              {history.isError ? <LoadFailed onRetry={() => void history.refetch()} /> : null}
              {history.data?.length === 0 ? <p className="text-sm text-muted-foreground">{t('deployments.noHistory')}</p> : null}
              {(history.data ?? []).map((m, i) => (
                <HistoryRow
                  key={m.migrationId + m.appliedAt}
                  migration={m}
                  latest={i === (history.data ?? []).findIndex((x) => !x.rolledBackAt) && !m.rolledBackAt}
                  canRollBack={canPromote}
                />
              ))}
            </TabsContent>
          </Tabs>
        </div>
      )}
    </Page>
  );
}

function Unpublished({ targets }: { targets: Environment[] }) {
  const { t } = useTranslation();
  const changes = useUnpublished(true);
  const cut = useCutMigration();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  // The changes ticked by hand the other way from their default: configuration starts in the
  // migration, published content starts out of it -- content moves only when chosen.
  const [flipped, setFlipped] = useState<ReadonlySet<string>>(new Set());
  const list = changes.data ?? [];
  const included = (c: ConfigChange) => (c.kind !== 'content') !== flipped.has(c.id);
  const chosen = list.filter(included);

  return (
    <section className="flex flex-col gap-3">
      <p className="text-sm text-muted-foreground">
        {targets.length > 0
          ? t('deployments.changesHint', { names: targets.map((e) => e.name).join(', ') })
          : t('deployments.changesHintNoTarget')}
      </p>
      {changes.isLoading ? <CenteredSpinner /> : null}
      {changes.isError ? <LoadFailed onRetry={() => void changes.refetch()} /> : null}
      {changes.data && list.length === 0 ? <p className="text-sm text-muted-foreground">{t('deployments.nothingChanged')}</p> : null}
      {list.length > 0 ? (
        <Card>
          <CardContent className="flex flex-col gap-2 pt-6">
            {list.map((c) => (
              <div key={c.id} className="flex items-start gap-2">
                <Checkbox
                  className="mt-2.5"
                  checked={included(c)}
                  aria-label={t('deployments.include', { name: c.label })}
                  onCheckedChange={() =>
                    setFlipped((prev) => {
                      const next = new Set(prev);
                      if (!next.delete(c.id)) next.add(c.id);
                      return next;
                    })
                  }
                />
                <div className="min-w-0 flex-1">
                  <ChangeRow change={c} />
                </div>
              </div>
            ))}
            <form
              className="mt-2 flex flex-col gap-2"
              onSubmit={(e) => {
                e.preventDefault();
                cut.mutate(
                  {
                    name: name.trim(),
                    description: description.trim() || undefined,
                    // Only when something is left out: absent means all of them, including any
                    // change made since this list was read.
                    changes: chosen.length < list.length ? chosen.map((c) => c.id) : undefined,
                  },
                  {
                    onSuccess: () => {
                      setName('');
                      setDescription('');
                      setFlipped(new Set());
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
                <Button type="submit" disabled={!name.trim() || chosen.length === 0 || cut.isPending}>
                  <GitCommitHorizontal className="h-4 w-4" /> {t('deployments.createMigration', { count: chosen.length })}
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
      <SectionHeader
        title={t('deployments.pendingFrom', { name: source.name })}
        description={list.length > 0 ? t('deployments.waiting', { count: list.length }) : undefined}
        actions={
          list.length > 1 ? (
            <Button
              onClick={() => deploy(list[list.length - 1].migration.migrationId)}
              disabled={apply.isPending || list.some((p) => p.changes.some((c) => !isResolved(p.migration.migrationId, c, resolutions)))}
            >
              <ArrowDownToLine className="h-4 w-4" /> {t('deployments.deployAll', { count: list.length })}
            </Button>
          ) : undefined
        }
      />
      {pending.isLoading ? <CenteredSpinner /> : null}
      {pending.isError ? <LoadFailed onRetry={() => void pending.refetch()} /> : null}
      {pending.data && list.length === 0 ? <p className="text-sm text-muted-foreground">{t('deployments.upToDate')}</p> : null}
      {list.map(({ migration, changes }, index) => {
        // Applying up to here applies everything before it too, so every conflict up to here counts.
        const unresolved = list
          .slice(0, index + 1)
          .some((p) => p.changes.some((c) => !isResolved(p.migration.migrationId, c, resolutions)));
        return (
          <Card key={migration.migrationId}>
            <CardContent className="flex flex-col gap-2 pt-6">
              <MigrationHeading migration={migration} />
              {changes.map((c) => (
                // One item can change in several pending migrations; each is decided on its own.
                <ChangeRow
                  key={c.id}
                  change={c}
                  migrationId={migration.migrationId}
                  resolutions={resolutions}
                  onResolve={(key, choice) => setResolutions((r) => ({ ...r, [key]: choice }))}
                />
              ))}
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
  const unresolved = changes.some((c) => !isResolved(migrationId, c, resolutions));
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
              migrationId={migrationId}
              resolutions={resolutions}
              onResolve={(key, choice) => setResolutions((r) => ({ ...r, [key]: choice }))}
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

const OUTCOME_TONE = {
  apply: 'default',
  already: 'secondary',
  merge: 'default',
  conflict: 'destructive',
  local: 'outline',
} as const;

/**
 * One change: what it is, how it lands here, and its before and after side by side. A change that
 * merges into what this environment changed itself shows the result; a conflict is decided for
 * the whole change, or path by path where both sides changed the same field.
 */
function ChangeRow({
  change,
  migrationId,
  resolutions,
  onResolve,
}: {
  change: ConfigChange;
  migrationId?: string;
  resolutions?: Resolutions;
  onResolve?: (key: string, choice: Resolutions[string]) => void;
}) {
  const { t } = useTranslation();
  const conflict = change.outcome === 'conflict';
  const merges = change.outcome === 'merge' || conflict;
  const wholeKey = `${migrationId}/${change.id}`;
  const paths = (change.conflicts ?? []).filter((c) => c.path !== '');
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
      <FieldDiff before={change.before} after={change.after} />
      {/* A conflict is decided against what is here now: its values open with it. */}
      <details className="mt-2" open={conflict}>
        <summary className="cursor-pointer text-xs text-muted-foreground">{t('deployments.fullJson')}</summary>
        <div className="mt-2 grid grid-cols-1 gap-2 md:grid-cols-2">
          <Json label={t('deployments.before')} value={change.before} />
          <Json label={t('deployments.after')} value={change.after} />
          {merges ? <Json label={t('deployments.current')} value={change.current} /> : null}
          {merges && change.merged !== undefined ? <Json label={t('deployments.merged')} value={change.merged} /> : null}
        </div>
      </details>
      {change.outcome === 'merge' && onResolve && migrationId ? (
        // A merge can still clash where git lays a site's code on; leaving it out here is a choice.
        <label className="mt-2 flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={resolutions?.[wholeKey] === 'skip'}
            onChange={(e) => onResolve(wholeKey, (e.target.checked ? 'skip' : undefined) as Resolutions[string])}
          />
          {t('deployments.skipHere')}
        </label>
      ) : null}
      {conflict && onResolve && migrationId ? (
        <div className="mt-2 flex flex-col gap-2 text-sm">
          <Choices
            name={`resolve-${migrationId}-${change.id}`}
            legend={t('deployments.resolve')}
            options={['overwrite', 'skip']}
            value={resolutions?.[wholeKey]}
            onChange={(choice) => onResolve(wholeKey, choice)}
          />
          {paths.length > 0 && !resolutions?.[wholeKey] ? (
            <div className="flex flex-col gap-2">
              <p className="text-xs text-muted-foreground">{t('deployments.orPerPath')}</p>
              {paths.map((p) => {
                const key = `${wholeKey}#${p.path}`;
                return (
                  <div key={p.path} className="rounded border p-2">
                    <p className="mb-1 break-all font-mono text-xs">{p.path}</p>
                    <div className="grid grid-cols-1 gap-2 md:grid-cols-2">
                      <Json label={t('deployments.ours')} value={p.ours} />
                      <Json label={t('deployments.theirs')} value={p.theirs} />
                    </div>
                    <Choices
                      name={`resolve-${migrationId}-${key}`}
                      legend={p.path}
                      options={['ours', 'theirs']}
                      value={resolutions?.[key]}
                      onChange={(choice) => onResolve(key, choice)}
                    />
                  </div>
                );
              })}
            </div>
          ) : null}
        </div>
      ) : null}
    </details>
  );
}

/** A value in a few words: a short string as it is, anything bigger by its size. */
function brief(value: unknown, t: (key: string, options?: Record<string, unknown>) => string): string {
  if (value == null) return '—';
  if (Array.isArray(value)) return t('deployments.items', { count: value.length });
  if (typeof value === 'object') return t('deployments.fields', { count: Object.keys(value).length });
  const text = String(value);
  return text.length > 80 ? `${text.slice(0, 77)}…` : text;
}

/**
 * What a change does, field by field: for an update the fields that differ, before and after;
 * for an addition or a removal the fields it brings or takes. The JSON stays a click away.
 */
function FieldDiff({ before, after }: { before: unknown; after: unknown }) {
  const { t } = useTranslation();
  const asObject = (v: unknown) => (v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null);
  const b = asObject(before);
  const a = asObject(after);
  if (!b && !a) {
    return (
      <p className="mt-2 text-sm">
        <span className="text-muted-foreground line-through">{brief(before, t)}</span> → {brief(after, t)}
      </p>
    );
  }
  const keys = [...new Set([...Object.keys(b ?? {}), ...Object.keys(a ?? {})])].filter(
    (k) => JSON.stringify(b?.[k]) !== JSON.stringify(a?.[k]),
  );
  if (keys.length === 0) return null;
  return (
    <dl className="mt-2 grid grid-cols-[minmax(0,auto)_minmax(0,1fr)] gap-x-3 gap-y-1 text-sm">
      {keys.slice(0, 12).map((k) => (
        <div key={k} className="contents">
          <dt className="font-mono text-xs text-muted-foreground [overflow-wrap:anywhere]">{k}</dt>
          <dd className="min-w-0 [overflow-wrap:anywhere]">
            {b && a ? (
              <>
                <span className="text-muted-foreground line-through">{brief(b[k], t)}</span> → {brief(a[k], t)}
              </>
            ) : (
              brief((a ?? b)?.[k], t)
            )}
          </dd>
        </div>
      ))}
      {keys.length > 12 ? <p className="col-span-2 text-xs text-muted-foreground">{t('deployments.moreFields', { count: keys.length - 12 })}</p> : null}
    </dl>
  );
}

function Choices<T extends Resolutions[string]>({
  name,
  legend,
  options,
  value,
  onChange,
}: {
  name: string;
  legend: string;
  options: T[];
  value?: Resolutions[string];
  onChange: (choice: T) => void;
}) {
  const { t } = useTranslation();
  return (
    <fieldset className="flex flex-wrap gap-4">
      <legend className="sr-only">{legend}</legend>
      {options.map((choice) => (
        <label key={choice} className="flex items-center gap-2">
          <input type="radio" name={name} checked={value === choice} onChange={() => onChange(choice)} />
          {t(`deployments.resolutions.${choice}`)}
        </label>
      ))}
    </fieldset>
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
