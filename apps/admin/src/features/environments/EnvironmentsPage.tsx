import { ArrowDown, ArrowUp, Link2, Lock, Pencil, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  Checkbox,
  Input,
  Label,
  LoadFailed,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  toastApiError,
  useConfirm,
} from '@dcms/ui';
import {
  useAddEnvironment,
  useCopyScopes,
  useDeleteEnvironment,
  useEnvironments,
  useLinkMutations,
  useLinks,
  usePolicies,
  useSavePolicies,
  useUpdateEnvironment,
  CONFIG_KINDS,
  POLICY_KINDS,
  type Environment,
  type Ownership,
  type Policy,
} from './api';

// Typed as a plain string: the router's route union is built from routes.tsx, which imports this page.
const DEPLOYMENTS: string = '/deployments';
const NAME = /^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$/;

/**
 * Settings → Environments (ADR 0028): adding them, their order and which of them may deploy
 * configuration into which. Each environment has its own configuration and content and shares the
 * workspace's members and media; the pill beside the workspace switcher moves between them.
 */
export function EnvironmentsPage() {
  const { t } = useTranslation();
  const environments = useEnvironments();
  const navigate = useNavigate();
  const add = useAddEnvironment();
  const [name, setName] = useState('');
  const [from, setFrom] = useState('');
  const [copy, setCopy] = useState<string[]>([]);
  const [feature, setFeature] = useState(false);
  const [deleteWhenMerged, setDeleteWhenMerged] = useState(true);
  const scopes = useCopyScopes();
  const data = environments.data;
  const list = data?.environments ?? [];

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    add.mutate(
      {
        name: name.trim(),
        from: from || undefined,
        copy: copy.length > 0 ? copy : undefined,
        ...(feature ? { feature: true, deleteWhenMerged } : {}),
      },
      {
        onSuccess: () => {
          setName('');
          setCopy([]);
        },
        onError: (err) => toastApiError(err, t),
      },
    );
  };

  return (
    <Page>
      <PageHeader
        title={t('environments.title')}
        description={t('environments.description')}
        actions={
          data?.enabled ? (
            <Button variant="outline" onClick={() => void navigate({ to: DEPLOYMENTS })}>
              {t('deployments.title')}
            </Button>
          ) : undefined
        }
      />
      {environments.isLoading ? <CenteredSpinner /> : null}
      {environments.isError ? <LoadFailed onRetry={() => void environments.refetch()} /> : null}

      {data ? (
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted-foreground">{t(data.enabled ? 'environments.pipelineHint' : 'environments.offHint')}</p>
          {list.map((env, index) => (
            <EnvironmentCard key={env.id} env={env} all={list} above={list[index - 1]} below={list[index + 1]} />
          ))}

          <form onSubmit={submit} className="flex flex-col gap-3">
            <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
              <div className="flex-1">
                <Label htmlFor="env-name">{t('environments.addLabel')}</Label>
                <Input
                  id="env-name"
                  value={name}
                  placeholder="development"
                  onChange={(e) => setName(e.target.value.toLowerCase())}
                />
              </div>
              <div className="sm:w-48">
                <Label htmlFor="env-from">{t('environments.copyFrom')}</Label>
                <Select value={from || (list.find((e) => e.isProduction)?.id ?? '')} onValueChange={setFrom}>
                  <SelectTrigger id="env-from">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {list.map((e) => (
                      <SelectItem key={e.id} value={e.id}>
                        {e.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <Button type="submit" disabled={!NAME.test(name.trim()) || add.isPending}>
                <Plus className="h-4 w-4" /> {t('environments.add')}
              </Button>
            </div>
            <div className="flex flex-col gap-1.5 sm:flex-row sm:gap-6">
              <label className="flex items-center gap-2 text-sm">
                <Checkbox checked={feature} onCheckedChange={(on) => setFeature(on === true)} />
                {t('environments.featureBranch')}
              </label>
              {feature ? (
                <label className="flex items-center gap-2 text-sm">
                  <Checkbox checked={deleteWhenMerged} onCheckedChange={(on) => setDeleteWhenMerged(on === true)} />
                  {t('environments.deleteWhenMerged')}
                </label>
              ) : null}
            </div>
            {feature ? <p className="text-xs text-muted-foreground">{t('environments.featureHint')}</p> : null}
            {scopes.data && scopes.data.length > 0 ? (
              <fieldset className="flex flex-col gap-1.5">
                <legend className="mb-1 text-xs font-medium text-muted-foreground">{t('environments.copyData')}</legend>
                <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
                  {scopes.data.map(({ scope, personalData }) => (
                    <label key={scope} className="flex items-center gap-2 text-sm">
                      <Checkbox
                        checked={copy.includes(scope)}
                        onCheckedChange={(on) => setCopy((prev) => (on === true ? [...prev, scope] : prev.filter((x) => x !== scope)))}
                      />
                      <span className="min-w-0">{t(`environments.copyScopes.${scope}`, { defaultValue: scope })}</span>
                      {personalData ? <Badge tone="warning">{t('environments.personalData')}</Badge> : null}
                    </label>
                  ))}
                </div>
                {copy.some((c) => scopes.data.find((x) => x.scope === c)?.personalData) ? (
                  <p className="text-xs text-muted-foreground">{t('environments.personalDataHint')}</p>
                ) : null}
              </fieldset>
            ) : null}
          </form>
          <p className="text-xs text-muted-foreground">{t('environments.addHint')}</p>
        </div>
      ) : null}
    </Page>
  );
}

function EnvironmentCard({
  env,
  all,
  above,
  below,
}: {
  env: Environment;
  all: Environment[];
  above?: Environment;
  below?: Environment;
}) {
  const { t } = useTranslation();
  const confirm = useConfirm();
  const update = useUpdateEnvironment();
  const remove = useDeleteEnvironment();
  const [renaming, setRenaming] = useState(false);
  const [draft, setDraft] = useState(env.name);
  const onError = (err: unknown) => toastApiError(err, t);

  // Swapping orders with a neighbour: the server keeps no positions of its own.
  const swap = (other: Environment) => {
    update.mutate({ id: env.id, order: other.order }, { onError });
    update.mutate({ id: other.id, order: env.order }, { onError });
  };
  const toggleSource = (source: string, on: boolean) =>
    update.mutate(
      {
        id: env.id,
        promotesFrom: on ? [...env.promotesFrom, source] : env.promotesFrom.filter((s) => s !== source),
      },
      { onError },
    );
  const destroy = async () => {
    const ok = await confirm({
      title: t('environments.deleteTitle', { name: env.name }),
      description: t('environments.deleteHint'),
      confirmLabel: t('actions.delete'),
      destructive: true,
    });
    if (ok) remove.mutate(env.id, { onError });
  };

  return (
    <Card>
      <CardContent className="flex flex-col gap-3 pt-6">
        <div className="flex flex-wrap items-center gap-2">
          {renaming ? (
            <form
              className="flex min-w-0 flex-1 gap-2"
              onSubmit={(e) => {
                e.preventDefault();
                update.mutate({ id: env.id, name: draft.trim() }, { onSuccess: () => setRenaming(false), onError });
              }}
            >
              <Input aria-label={t('environments.name')} value={draft} onChange={(e) => setDraft(e.target.value.toLowerCase())} />
              <Button type="submit" size="sm" disabled={!NAME.test(draft.trim())}>
                {t('actions.save')}
              </Button>
            </form>
          ) : (
            <>
              <h2 className="min-w-0 truncate text-base font-semibold">{env.name}</h2>
              {env.isProduction ? <Badge tone="destructive">{t('environments.root')}</Badge> : null}
              {env.forkedFrom ? (
                <Badge tone="outline">
                  {t('environments.featureOf', { name: all.find((e) => e.id === env.forkedFrom)?.name ?? '?' })}
                </Badge>
              ) : null}
            </>
          )}
          <div className="ml-auto flex gap-1">
            <Button variant="ghost" size="icon" aria-label={t('environments.moveUp')} disabled={!above} onClick={() => above && swap(above)}>
              <ArrowUp className="h-4 w-4" />
            </Button>
            <Button variant="ghost" size="icon" aria-label={t('environments.moveDown')} disabled={!below} onClick={() => below && swap(below)}>
              <ArrowDown className="h-4 w-4" />
            </Button>
            <Button variant="ghost" size="icon" aria-label={t('environments.rename')} onClick={() => setRenaming((r) => !r)}>
              <Pencil className="h-4 w-4" />
            </Button>
            {!env.isProduction ? (
              <Button variant="ghost" size="icon" aria-label={t('actions.delete')} onClick={() => void destroy()}>
                <Trash2 className="h-4 w-4" />
              </Button>
            ) : null}
          </div>
        </div>

        {all.length > 1 ? <OwnershipGrid environmentId={env.id} /> : null}
        {all.length > 1 ? <Links env={env} all={all} /> : null}

        {all.length > 1 ? (
          <fieldset className="flex flex-col gap-1.5">
            <legend className="mb-1 text-xs font-medium text-muted-foreground">{t('environments.receivesFrom')}</legend>
            <div className="flex flex-wrap gap-x-4 gap-y-1.5">
              {all
                .filter((other) => other.id !== env.id)
                .map((other) => {
                  const id = `from-${env.id}-${other.id}`;
                  return (
                    <label key={other.id} htmlFor={id} className="flex items-center gap-2 text-sm">
                      <Checkbox
                        id={id}
                        checked={env.promotesFrom.includes(other.id)}
                        onCheckedChange={(on) => toggleSource(other.id, on === true)}
                      />
                      {other.name}
                    </label>
                  );
                })}
            </div>
          </fieldset>
        ) : null}
      </CardContent>
    </Card>
  );
}

/**
 * Who owns each kind of configuration here: edited here and deployed into (mixed), edited here
 * only (local), or deployments only (managed). Item-level exceptions set through the API are kept.
 */
function OwnershipGrid({ environmentId }: { environmentId: string }) {
  const { t } = useTranslation();
  const policies = usePolicies(environmentId);
  const save = useSavePolicies(environmentId);
  const list = policies.data ?? [];
  const modeOf = (kind: string): Ownership => list.find((p) => p.kind === kind && p.itemKey == null)?.mode ?? 'Mixed';
  const write = (next: Policy[]) => save.mutate(next, { onError: (err) => toastApiError(err, t) });
  const set = (kind: string, mode: Ownership) =>
    write([...list.filter((p) => !(p.kind === kind && p.itemKey == null)), { kind, itemKey: null, mode }]);
  const lockAll = () =>
    write([
      ...list.filter((p) => p.itemKey != null),
      ...CONFIG_KINDS.map((kind): Policy => ({ kind, itemKey: null, mode: 'Managed' })),
    ]);

  if (policies.isError) return <LoadFailed onRetry={() => void policies.refetch()} />;
  return (
    <fieldset className="flex flex-col gap-1.5" disabled={!policies.data || save.isPending}>
      <legend className="mb-1 text-xs font-medium text-muted-foreground">{t('environments.ownership')}</legend>
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        {POLICY_KINDS.map((kind) => (
          <label key={kind} className="flex items-center justify-between gap-2 text-sm">
            <span className="min-w-0 truncate">{t(`deployments.kinds.${kind}`)}</span>
            <Select value={modeOf(kind)} onValueChange={(mode) => set(kind, mode as Ownership)}>
              <SelectTrigger className="w-40" aria-label={t('environments.ownershipOf', { kind: t(`deployments.kinds.${kind}`) })}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {(['Mixed', 'Local', 'Managed'] as const).map((mode) => (
                  <SelectItem key={mode} value={mode}>
                    {t(`environments.modes.${mode}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </label>
        ))}
      </div>
      <div>
        <Button variant="outline" size="sm" onClick={lockAll}>
          <Lock className="h-4 w-4" /> {t('environments.lockToPipeline')}
        </Button>
      </div>
    </fieldset>
  );
}

/**
 * What this environment uses from another one (ADR 0026): apps — its site's /api/{address} is
 * served by that environment's app, production's real articles in development, say — read only, or
 * read and write; and site users — its sites sign people in through that environment's users.
 */
function Links({ env, all }: { env: Environment; all: Environment[] }) {
  const { t } = useTranslation();
  const links = useLinks(env.id);
  const { add, remove } = useLinkMutations(env.id);
  const others = all.filter((e) => e.id !== env.id);
  const [target, setTarget] = useState(others[0]?.id ?? '');
  const [address, setAddress] = useState('');
  const [access, setAccess] = useState<'read' | 'readWrite'>('read');
  const [usersFrom, setUsersFrom] = useState(others[0]?.id ?? '');
  const nameOf = (id: string) => all.find((e) => e.id === id)?.name ?? id;
  const onError = (err: unknown) => toastApiError(err, t);
  const usersLink = (links.data ?? []).find((l) => l.kind === 'users');

  return (
    <fieldset className="flex flex-col gap-1.5">
      <legend className="mb-1 text-xs font-medium text-muted-foreground">{t('environments.links')}</legend>
      {usersLink ? (
        <div className="flex items-center gap-2 text-sm">
          <Link2 className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
          <span className="min-w-0 truncate">{t('environments.usersLinkRow', { name: nameOf(usersLink.targetEnvironmentId) })}</span>
          <Button
            variant="ghost"
            size="icon"
            className="ml-auto"
            aria-label={t('environments.usersUnlink')}
            onClick={() => remove.mutate(usersLink.id, { onError })}
          >
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      ) : (
        <form
          className="flex flex-col gap-2 sm:flex-row sm:items-center"
          onSubmit={(e) => {
            e.preventDefault();
            add.mutate({ kind: 'users', targetEnvironmentId: usersFrom }, { onError });
          }}
        >
          <span className="text-sm text-muted-foreground sm:flex-1">{t('environments.usersLinkHint')}</span>
          <Select value={usersFrom} onValueChange={setUsersFrom}>
            <SelectTrigger className="sm:w-40" aria-label={t('environments.linkFrom')}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {others.map((o) => (
                <SelectItem key={o.id} value={o.id}>
                  {o.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button type="submit" variant="outline" disabled={!usersFrom || add.isPending}>
            <Link2 className="h-4 w-4" /> {t('environments.usersLink')}
          </Button>
        </form>
      )}
      {(links.data ?? []).filter((l) => l.kind === 'app').map((l) => (
        <div key={l.id} className="flex items-center gap-2 text-sm">
          <Link2 className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
          <span className="min-w-0 truncate">
            {t('environments.linkRow', { address: l.targetKey, name: nameOf(l.targetEnvironmentId) })}
          </span>
          <Badge tone="outline">{t(`environments.access.${l.access}`)}</Badge>
          <Button
            variant="ghost"
            size="icon"
            className="ml-auto"
            aria-label={t('environments.unlink', { address: l.targetKey })}
            onClick={() => remove.mutate(l.id, { onError })}
          >
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      ))}
      <form
        className="flex flex-col gap-2 sm:flex-row sm:items-center"
        onSubmit={(e) => {
          e.preventDefault();
          add.mutate(
            { kind: 'app', targetEnvironmentId: target, targetKey: address.trim(), access },
            { onSuccess: () => setAddress(''), onError },
          );
        }}
      >
        <Input
          aria-label={t('environments.linkAddress')}
          placeholder={t('environments.linkAddress')}
          value={address}
          onChange={(e) => setAddress(e.target.value.toLowerCase())}
        />
        <Select value={target} onValueChange={setTarget}>
          <SelectTrigger className="sm:w-40" aria-label={t('environments.linkFrom')}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {others.map((o) => (
              <SelectItem key={o.id} value={o.id}>
                {o.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={access} onValueChange={(v) => setAccess(v as 'read' | 'readWrite')}>
          <SelectTrigger className="sm:w-40" aria-label={t('environments.linkAccess')}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="read">{t('environments.access.Read')}</SelectItem>
            <SelectItem value="readWrite">{t('environments.access.ReadWrite')}</SelectItem>
          </SelectContent>
        </Select>
        <Button type="submit" variant="outline" disabled={!address.trim() || !target || add.isPending}>
          <Link2 className="h-4 w-4" /> {t('environments.link')}
        </Button>
      </form>
    </fieldset>
  );
}
