import { ArrowDown, ArrowRight, ArrowUp, Check, ChevronRight, Circle, Layers, Link2, Loader2, Lock, LockOpen, Plus, Server, Trash2 } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearch } from '@tanstack/react-router';
import {
  Badge,
  Button,
  Callout,
  Card,
  CardContent,
  CenteredSpinner,
  Checkbox,
  EmptyState,
  Input,
  Label,
  LoadFailed,
  Page,
  PageHeader,
  Progress,
  SectionHeader,
  Switch,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  cn,
  toastApiError,
  useConfirm,
} from '@dcms/ui';
import { Breadcrumbs } from '../../app/Breadcrumbs';
import { useJobs } from '../jobs/api';
import { useTabParam } from '../../lib/useTabParam';
import { LinksPanel } from './LinksPanel';
import { LocksPanel } from './LocksPanel';
import { ENVIRONMENT_NAME, NewEnvironmentDialog } from './NewEnvironmentDialog';
import { lockState, useDeleteEnvironment, useEnvironments, useLinks, usePolicies, useUpdateEnvironment, type Environment, type SetupStep } from './api';

// Typed as plain strings: the router's route union is built from routes.tsx, which imports this page.
const DEPLOYMENTS: string = '/deployments';
const SELF: string = '/settings/environments';
const TABS = ['overview', 'locks', 'links'] as const;

const COPY = 'environments.copy';

/**
 * Copies that failed in the last day. A failed copy removes its environment again, so the reason is
 * kept in its job and said here -- nowhere else would it show.
 */
function useFailedCopies() {
  const jobs = useJobs('failed', 20);
  const since = Date.now() - 24 * 3600 * 1000;
  return (jobs.data?.items ?? []).filter((j) => j.kind === COPY && Date.parse(j.createdAt) > since);
}

/** Says so when an environment being set up opens. */
function useReadyToasts(list: Environment[]) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const waiting = useRef(new Set<string>());
  useEffect(() => {
    for (const e of list) {
      if (e.ready === false) waiting.current.add(e.id);
      else if (waiting.current.delete(e.id)) {
        toast.success(t('environments.readyToast', { name: e.name }));
        void qc.invalidateQueries({ queryKey: ['me-tenants'] });
      }
    }
  }, [list, t, qc]);
}

/** Steps finished of all of them, for a bar. */
function setupProgress(steps: SetupStep[] | null | undefined) {
  const all = steps ?? [];
  return { done: all.filter((x) => x.state === 'done').length, count: all.length };
}

/**
 * Settings → Environments (ADR 0028): the workspace's pipeline at a glance, and each environment on
 * a page of its own (`?env=`) -- what it is, who may change what in it, and what it uses from the
 * others. Creating one is a dialog of three decisions rather than a form under the list.
 */
export function EnvironmentsPage() {
  const { t } = useTranslation();
  const environments = useEnvironments();
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as { env?: string };
  const [creating, setCreating] = useState(false);
  const [dismissed, setDismissed] = useState<string[]>([]);
  const list = environments.data?.environments ?? [];
  useReadyToasts(list);
  // A copy that failed removed its environment again: what stopped it is said here.
  const failed = useFailedCopies().filter((j) => !list.some((e) => e.name === j.params?.environment) && !dismissed.includes(j.id));
  const open = (name: string | undefined) => void navigate({ to: SELF, search: (name ? { env: name } : {}) as never });
  const selected = search.env ? list.find((e) => e.name === search.env) : undefined;

  const dialog = creating ? (
    <NewEnvironmentDialog
      all={list}
      onClose={() => setCreating(false)}
      onCreated={(env) => {
        setCreating(false);
        open(env.name);
      }}
    />
  ) : null;

  if (selected) {
    return (
      <>
        <EnvironmentDetail env={selected} all={list} onDeleted={() => open(undefined)} />
        {dialog}
      </>
    );
  }

  return (
    <Page>
      <PageHeader
        title={t('environments.title')}
        description={t('environments.description')}
        actions={
          <>
            {environments.data?.enabled ? (
              <Button variant="outline" onClick={() => void navigate({ to: DEPLOYMENTS })}>
                {t('deployments.title')}
              </Button>
            ) : null}
            <Button onClick={() => setCreating(true)} disabled={!environments.data}>
              <Plus className="h-4 w-4" /> {t('environments.new.title')}
            </Button>
          </>
        }
      />
      {environments.isLoading ? <CenteredSpinner /> : null}
      {environments.isError ? <LoadFailed onRetry={() => void environments.refetch()} /> : null}
      {failed.map((j) => (
        <div key={j.id} className="mb-4">
          <Callout tone="destructive" title={t('environments.copyFailed', { name: String(j.params?.environment ?? '') })}>
            <p>{t(j.errorKey ?? 'jobs.error.failed')}</p>
            {j.errorDetail ? <p className="mt-1 font-mono text-xs [overflow-wrap:anywhere]">{j.errorDetail}</p> : null}
            <Button variant="outline" size="sm" className="mt-2" onClick={() => setDismissed((d) => [...d, j.id])}>
              {t('environments.dismiss')}
            </Button>
          </Callout>
        </div>
      ))}
      {environments.data && !environments.data.enabled ? (
        <EmptyState
          icon={Layers}
          title={t('environments.emptyTitle')}
          description={t('environments.offHint')}
          action={
            <Button onClick={() => setCreating(true)}>
              <Plus className="h-4 w-4" /> {t('environments.new.title')}
            </Button>
          }
        />
      ) : null}
      {environments.data?.enabled ? <Overview all={list} onOpen={open} /> : null}
      {dialog}
    </Page>
  );
}

/**
 * The pipeline left to right, as configuration travels it; each stage's features beneath it, and
 * the services, which sit outside it, apart.
 */
function Overview({ all, onOpen }: { all: Environment[]; onOpen: (name: string) => void }) {
  const { t } = useTranslation();
  const stages = all.filter((e) => e.purpose === 'stage').sort((a, b) => a.order - b.order);
  const features = all.filter((e) => e.purpose === 'feature');
  const services = all.filter((e) => e.purpose === 'service');

  return (
    <div className="flex flex-col gap-8">
      <section className="flex flex-col gap-3">
        <SectionHeader title={t('environments.pipeline')} description={t('environments.pipelineHint')} />
        <ol aria-label={t('environments.pipeline')} className="flex flex-col gap-2 lg:flex-row lg:items-stretch">
          {stages.map((stage, i) => (
            <li key={stage.id} className="flex flex-col gap-2 lg:flex-1 lg:flex-row lg:items-center">
              <div className="min-w-0 flex-1 self-stretch">
                <EnvironmentTile env={stage} all={all} onOpen={onOpen} />
              </div>
              {i < stages.length - 1 ? (
                <span aria-hidden className="flex justify-center text-muted-foreground">
                  <ArrowDown className="h-4 w-4 lg:hidden" />
                  <ArrowRight className="hidden h-4 w-4 lg:block" />
                </span>
              ) : null}
            </li>
          ))}
        </ol>
      </section>

      {features.length > 0 ? (
        <section className="flex flex-col gap-3">
          <SectionHeader title={t('environments.features')} description={t('environments.featuresHint')} />
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
            {features.map((f) => (
              <EnvironmentTile key={f.id} env={f} all={all} onOpen={onOpen} />
            ))}
          </div>
        </section>
      ) : null}

      {services.length > 0 ? (
        <section className="flex flex-col gap-3">
          <SectionHeader title={t('environments.services')} description={t('environments.servicesHint')} />
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
            {services.map((s) => (
              <EnvironmentTile key={s.id} env={s} all={all} onOpen={onOpen} />
            ))}
          </div>
        </section>
      ) : null}
    </div>
  );
}

/** One environment as the overview shows it: what it is, how locked, what it uses -- and the way in. */
function EnvironmentTile({ env, all, onOpen }: { env: Environment; all: Environment[]; onOpen: (name: string) => void }) {
  const { t } = useTranslation();
  const policies = usePolicies(env.id);
  const links = useLinks(env.id);
  const lock = policies.data ? lockState(policies.data) : null;
  const sources = all.filter((e) => env.promotesFrom.includes(e.id)).map((e) => e.name);
  return (
    <button
      type="button"
      onClick={() => onOpen(env.name)}
      className={cn(
        'group flex h-full w-full items-start gap-3 rounded-lg border bg-card p-3 text-left',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring can-hover:hover:border-primary/50',
        env.isProduction && 'border-primary/40',
      )}
    >
      <span className="min-w-0 flex-1 space-y-1.5">
        <span className="flex flex-wrap items-center gap-1.5">
          <span className="min-w-0 font-semibold [overflow-wrap:anywhere]">{env.name}</span>
          <PurposeBadge env={env} />
        </span>
        <span className="block text-xs text-muted-foreground">
          {env.purpose === 'service'
            ? t('environments.tile.outside')
            : env.purpose === 'feature'
              ? t('environments.tile.branchOf', { name: all.find((e) => e.id === env.forkedFrom)?.name ?? '?' })
              : sources.length > 0
              ? t('environments.tile.takesFrom', { names: sources.join(', ') })
              : t('environments.tile.takesNothing')}
        </span>
        <span className="flex flex-wrap gap-1.5">
          {env.ready === false ? (
            <Badge tone="warning">
              <Loader2 className="h-3 w-3 animate-spin motion-reduce:animate-none" aria-hidden />
              {t('environments.settingUpCount', setupProgress(env.setup))}
            </Badge>
          ) : null}
          {lock ? (
            <Badge tone={lock === 'locked' ? 'secondary' : 'outline'}>
              {lock === 'open' ? <LockOpen className="h-3 w-3" aria-hidden /> : <Lock className="h-3 w-3" aria-hidden />}
              {t(`environments.tile.lock.${lock}`)}
            </Badge>
          ) : null}
          {links.data && links.data.length > 0 ? (
            <Badge tone="outline">
              <Link2 className="h-3 w-3" aria-hidden /> {t('environments.tile.links', { count: links.data.length })}
            </Badge>
          ) : null}
        </span>
      </span>
      <ChevronRight className="mt-1 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
    </button>
  );
}

function PurposeBadge({ env }: { env: Environment }) {
  const { t } = useTranslation();
  if (env.isProduction) return <Badge tone="destructive">{t('environments.root')}</Badge>;
  if (env.purpose === 'feature') return <Badge tone="outline">{t('environments.purposes.feature')}</Badge>;
  if (env.purpose === 'service') {
    return (
      <Badge tone="outline">
        <Server className="h-3 w-3" aria-hidden /> {t('environments.purposes.service')}
      </Badge>
    );
  }
  return null;
}

/** One environment: its settings, its locks, its links -- each a tab, kept in the URL. */
function EnvironmentDetail({ env, all, onDeleted }: { env: Environment; all: Environment[]; onDeleted: () => void }) {
  const { t } = useTranslation();
  const [tab, setTab] = useTabParam('tab', TABS, 'overview');
  const deploysInto = all.filter((e) => e.promotesFrom.includes(env.id)).map((e) => e.name);
  const takesFrom = all.filter((e) => env.promotesFrom.includes(e.id)).map((e) => e.name);
  const summary =
    env.purpose === 'service'
      ? t('environments.detail.service')
      : [
          takesFrom.length > 0 ? t('environments.tile.takesFrom', { names: takesFrom.join(', ') }) : null,
          deploysInto.length > 0 ? t('environments.detail.deploysInto', { names: deploysInto.join(', ') }) : null,
        ]
          .filter(Boolean)
          .join('. ');

  return (
    <Page>
      <PageHeader
        crumbs={<Breadcrumbs items={[{ label: t('environments.title'), to: SELF }, { label: env.name }]} />}
        title={
          <span className="flex flex-wrap items-center gap-2">
            {env.name}
            <PurposeBadge env={env} />
          </span>
        }
        description={summary || undefined}
      />
      {env.ready === false ? <SetupPanel env={env} /> : null}
      {env.ready !== false ? (
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList>
          {TABS.map((v) => (
            <TabsTrigger key={v} value={v}>
              {t(`environments.tabs.${v}`)}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="overview">
          <OverviewTab env={env} all={all} onDeleted={onDeleted} />
        </TabsContent>
        <TabsContent value="locks">
          <LocksPanel environmentId={env.id} />
        </TabsContent>
        <TabsContent value="links">
          <LinksPanel env={env} all={all} />
        </TabsContent>
      </Tabs>
      ) : null}
    </Page>
  );
}

function OverviewTab({ env, all, onDeleted }: { env: Environment; all: Environment[]; onDeleted: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const confirm = useConfirm();
  const update = useUpdateEnvironment();
  const remove = useDeleteEnvironment();
  const [name, setName] = useState(env.name);
  const onError = (err: unknown) => toastApiError(err, t);
  const stages = all.filter((e) => e.purpose === 'stage').sort((a, b) => a.order - b.order);
  const at = stages.findIndex((e) => e.id === env.id);
  const others = all.filter((e) => e.id !== env.id);

  // Swapping orders with a neighbour: the server keeps no positions of its own.
  const swap = (other: Environment) => {
    update.mutate({ id: env.id, order: other.order }, { onError });
    update.mutate({ id: other.id, order: env.order }, { onError });
  };
  const toggleSource = (source: string, on: boolean) =>
    update.mutate(
      { id: env.id, promotesFrom: on ? [...env.promotesFrom, source] : env.promotesFrom.filter((s) => s !== source) },
      { onError },
    );
  const destroy = async () => {
    const ok = await confirm({
      title: t('environments.deleteTitle', { name: env.name }),
      description: t('environments.deleteHint'),
      confirmLabel: t('actions.delete'),
      destructive: true,
    });
    if (ok) remove.mutate(env.id, { onSuccess: onDeleted, onError });
  };

  return (
    <div className="flex max-w-3xl flex-col gap-6">
      <section className="flex flex-col gap-3">
        <SectionHeader title={t('environments.detail.name')} />
        <form
          className="flex flex-col gap-2 sm:flex-row sm:items-end"
          onSubmit={(e) => {
            e.preventDefault();
            update.mutate(
              { id: env.id, name: name.trim() },
              { onSuccess: () => void navigate({ to: SELF, search: { env: name.trim() } as never, replace: true }), onError },
            );
          }}
        >
          <div className="flex-1 space-y-1.5">
            <Label htmlFor="env-name">{t('environments.name')}</Label>
            <Input id="env-name" value={name} onChange={(e) => setName(e.target.value.toLowerCase())} />
          </div>
          <Button type="submit" variant="outline" disabled={name.trim() === env.name || !ENVIRONMENT_NAME.test(name.trim()) || update.isPending}>
            {t('environments.rename')}
          </Button>
        </form>
      </section>

      {env.purpose !== 'service' ? (
        <section className="flex flex-col gap-3">
          <SectionHeader title={t('environments.receivesFrom')} description={t('environments.detail.receivesHint')} />
          <Card>
            <CardContent className="grid grid-cols-1 gap-2 pt-6 sm:grid-cols-2">
              {others.map((other) => {
                const id = `from-${env.id}-${other.id}`;
                return (
                  <label key={other.id} htmlFor={id} className="flex items-center gap-2 text-sm">
                    <Checkbox id={id} checked={env.promotesFrom.includes(other.id)} onCheckedChange={(on) => toggleSource(other.id, on === true)} />
                    <span className="min-w-0 [overflow-wrap:anywhere]">{other.name}</span>
                  </label>
                );
              })}
            </CardContent>
          </Card>
          {env.purpose === 'stage' && stages.length > 1 ? (
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm text-muted-foreground">{t('environments.detail.position', { at: at + 1, count: stages.length })}</span>
              <Button variant="outline" size="sm" disabled={at <= 0} onClick={() => swap(stages[at - 1])}>
                <ArrowUp className="h-4 w-4" /> {t('environments.moveUp')}
              </Button>
              <Button variant="outline" size="sm" disabled={at < 0 || at >= stages.length - 1} onClick={() => swap(stages[at + 1])}>
                <ArrowDown className="h-4 w-4" /> {t('environments.moveDown')}
              </Button>
            </div>
          ) : null}
        </section>
      ) : null}

      {env.purpose === 'feature' ? (
        <section className="flex flex-col gap-3">
          <SectionHeader
            title={t('environments.detail.feature')}
            description={t('environments.featureOf', { name: all.find((e) => e.id === env.forkedFrom)?.name ?? '?' })}
          />
          <Card>
            <CardContent className="flex flex-col gap-3 pt-6">
              <label className="flex items-start gap-3">
                <Switch
                  className="mt-0.5"
                  checked={env.mergesOwnChangesOnly}
                  onCheckedChange={(on) => update.mutate({ id: env.id, mergesOwnChangesOnly: on }, { onError })}
                />
                <span className="text-sm">
                  <span className="block font-medium">{t('environments.detail.ownOnly')}</span>
                  <span className="block text-xs text-muted-foreground">{t('environments.detail.ownOnlyHint')}</span>
                </span>
              </label>
              <label className="flex items-start gap-3">
                <Switch
                  className="mt-0.5"
                  checked={env.deleteWhenMerged}
                  onCheckedChange={(on) => update.mutate({ id: env.id, deleteWhenMerged: on }, { onError })}
                />
                <span className="text-sm">
                  <span className="block font-medium">{t('environments.deleteWhenMerged')}</span>
                  <span className="block text-xs text-muted-foreground">{t('environments.new.deleteWhenMergedHint')}</span>
                </span>
              </label>
            </CardContent>
          </Card>
        </section>
      ) : null}

      {!env.isProduction ? (
        <section className="flex flex-col gap-3">
          <SectionHeader title={t('environments.detail.danger')} />
          <Card className="border-destructive/40">
            <CardContent className="flex flex-col gap-3 pt-6 sm:flex-row sm:items-center">
              <p className="min-w-0 flex-1 text-sm text-muted-foreground">{t('environments.deleteHint')}</p>
              <Button variant="destructive" onClick={() => void destroy()}>
                <Trash2 className="h-4 w-4" /> {t('environments.detail.delete', { name: env.name })}
              </Button>
            </CardContent>
          </Card>
        </section>
      ) : null}
    </div>
  );
}

/**
 * A new environment as it is being made: each step of the copy, what it is on now and how far it
 * got. It opens -- and this page turns into its settings -- once every step is done.
 */
function SetupPanel({ env }: { env: Environment }) {
  const { t } = useTranslation();
  const steps = env.setup ?? [];
  const { done, count } = setupProgress(steps);
  const label = (key: string) => {
    const [group, name] = [key.slice(0, key.indexOf(':')), key.slice(key.indexOf(':') + 1)];
    return group === 'kind'
      ? t(`deployments.kinds.${name}`, { defaultValue: name })
      : t(`environments.copyScopes.${name}`, { defaultValue: name });
  };
  const detail = (step: SetupStep) => {
    if (step.state === 'waiting') return t('environments.setup.waiting');
    if (step.key.startsWith('data:')) {
      return step.state === 'done' ? t('environments.setup.rows', { count: step.done }) : t('environments.setup.copying');
    }
    if (step.state === 'running') {
      return t('environments.setup.item', { item: step.item ?? '', at: step.done + 1, count: step.total ?? 0 });
    }
    return step.total ? t('environments.setup.copied', { count: step.total }) : t('environments.setup.none');
  };
  return (
    <Card>
      <CardContent className="flex flex-col gap-4 pt-6">
        <div className="space-y-2">
          <p className="font-medium">{t('environments.settingUpTitle')}</p>
          <p className="text-sm text-muted-foreground">{t('environments.settingUpHint')}</p>
          <Progress value={count > 0 ? (done / count) * 100 : 0} aria-label={t('environments.settingUpCount', { done, count })} />
          <p className="text-xs text-muted-foreground">{steps.length > 0 ? t('environments.settingUpCount', { done, count }) : t('environments.setup.queued')}</p>
        </div>
        {steps.length > 0 ? (
          <ol className="divide-y rounded-md border">
            {steps.map((step) => (
              <li key={step.key} className="flex items-center gap-3 px-3 py-2 text-sm">
                {step.state === 'done' ? (
                  <Check className="h-4 w-4 shrink-0 text-success-foreground" aria-hidden />
                ) : step.state === 'running' ? (
                  <Loader2 className="h-4 w-4 shrink-0 animate-spin text-primary motion-reduce:animate-none" aria-hidden />
                ) : (
                  <Circle className="h-4 w-4 shrink-0 text-muted-foreground/50" aria-hidden />
                )}
                <span className={cn('min-w-0 flex-1', step.state === 'waiting' && 'text-muted-foreground')}>{label(step.key)}</span>
                <span className="min-w-0 text-right text-xs text-muted-foreground [overflow-wrap:anywhere]">{detail(step)}</span>
              </li>
            ))}
          </ol>
        ) : null}
      </CardContent>
    </Card>
  );
}
