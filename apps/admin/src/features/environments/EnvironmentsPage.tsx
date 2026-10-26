import { ArrowDown, ArrowUp, Layers, Pencil, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Button,
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
  toastApiError,
  useConfirm,
} from '@dcms/ui';
import {
  useAddEnvironment,
  useDeleteEnvironment,
  useDisableEnvironments,
  useEnableEnvironments,
  useEnvironments,
  useUpdateEnvironment,
  type Environment,
} from './api';

const NAME = /^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$/;

/**
 * Settings → Environments (ADR 0026): turning environments on, adding them, their order and which
 * of them may promote configuration into which. Each environment is a separate copy of the
 * workspace with its own data; the pill beside the workspace switcher moves between them.
 */
export function EnvironmentsPage() {
  const { t } = useTranslation();
  const environments = useEnvironments();
  const enable = useEnableEnvironments();
  const disable = useDisableEnvironments();
  const add = useAddEnvironment();
  const [name, setName] = useState('');
  const data = environments.data;
  const list = data?.environments ?? [];

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    add.mutate(name.trim(), { onSuccess: () => setName(''), onError: (err) => toastApiError(err, t) });
  };

  return (
    <Page>
      <PageHeader title={t('environments.title')} description={t('environments.description')} />
      {environments.isLoading ? <CenteredSpinner /> : null}
      {environments.isError ? <LoadFailed onRetry={() => void environments.refetch()} /> : null}

      {data && !data.enabled ? (
        <EmptyState
          icon={Layers}
          title={t('environments.offTitle')}
          description={t('environments.offHint')}
          action={
            <Button onClick={() => enable.mutate(undefined, { onError: (err) => toastApiError(err, t) })} disabled={enable.isPending}>
              {t('environments.enable')}
            </Button>
          }
        />
      ) : null}

      {data?.enabled ? (
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted-foreground">{t('environments.pipelineHint')}</p>
          {list.map((env, index) => (
            <EnvironmentCard key={env.tenantId} env={env} all={list} above={list[index - 1]} below={list[index + 1]} />
          ))}

          <form onSubmit={submit} className="flex flex-col gap-2 sm:flex-row sm:items-end">
            <div className="flex-1">
              <Label htmlFor="env-name">{t('environments.addLabel')}</Label>
              <Input
                id="env-name"
                value={name}
                placeholder="development"
                onChange={(e) => setName(e.target.value.toLowerCase())}
              />
            </div>
            <Button type="submit" disabled={!NAME.test(name.trim()) || add.isPending}>
              <Plus className="h-4 w-4" /> {t('environments.add')}
            </Button>
          </form>
          <p className="text-xs text-muted-foreground">{t('environments.addHint')}</p>

          {list.length === 1 ? (
            <div>
              <Button variant="outline" onClick={() => disable.mutate(undefined, { onError: (err) => toastApiError(err, t) })} disabled={disable.isPending}>
                {t('environments.disable')}
              </Button>
            </div>
          ) : null}
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
    update.mutate({ id: env.tenantId, order: other.order }, { onError });
    update.mutate({ id: other.tenantId, order: env.order }, { onError });
  };
  const toggleSource = (source: string, on: boolean) =>
    update.mutate(
      {
        id: env.tenantId,
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
    if (ok) remove.mutate(env.tenantId, { onError });
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
                update.mutate({ id: env.tenantId, name: draft.trim() }, { onSuccess: () => setRenaming(false), onError });
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
              {env.isRoot ? <Badge tone="destructive">{t('environments.root')}</Badge> : null}
              <span className="min-w-0 truncate text-xs text-muted-foreground">{env.slug}</span>
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
            {!env.isRoot ? (
              <Button variant="ghost" size="icon" aria-label={t('actions.delete')} onClick={() => void destroy()}>
                <Trash2 className="h-4 w-4" />
              </Button>
            ) : null}
          </div>
        </div>

        {all.length > 1 ? (
          <fieldset className="flex flex-col gap-1.5">
            <legend className="mb-1 text-xs font-medium text-muted-foreground">{t('environments.receivesFrom')}</legend>
            <div className="flex flex-wrap gap-x-4 gap-y-1.5">
              {all
                .filter((other) => other.tenantId !== env.tenantId)
                .map((other) => {
                  const id = `from-${env.tenantId}-${other.tenantId}`;
                  return (
                    <label key={other.tenantId} htmlFor={id} className="flex items-center gap-2 text-sm">
                      <Checkbox
                        id={id}
                        checked={env.promotesFrom.includes(other.tenantId)}
                        onCheckedChange={(on) => toggleSource(other.tenantId, on === true)}
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
