import { Database, Globe, Plug, Trash2, UserRound, type LucideIcon } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Button,
  Callout,
  Card,
  CardContent,
  CenteredSpinner,
  Checkbox,
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  LoadFailed,
  SectionHeader,
  toastApiError,
} from '@dcms/ui';
import { ChoiceList } from './ChoiceList';
import { useLinkMutations, useLinks, useOffers, type Environment, type EnvironmentLink, type OfferedApp } from './api';

type Kind = EnvironmentLink['kind'];
const KINDS: readonly Kind[] = ['users', 'app', 'contracts', 'tables'];
const ICONS: Record<Kind, LucideIcon> = { users: UserRound, app: Globe, contracts: Plug, tables: Database };
const VERBS = ['read', 'create', 'update', 'delete'] as const;
// The plugin that signs site users in; an environment without it has no users to lend.
const USER_AUTH = 'user-auth';

/** `companies: read, create` per table, from a tables link's `table:verb` grants. */
function byTable(grants: string[]) {
  const tables = new Map<string, string[]>();
  for (const g of grants) {
    const at = g.lastIndexOf(':');
    const table = g.slice(0, at);
    tables.set(table, [...(tables.get(table) ?? []), g.slice(at + 1)]);
  }
  return [...tables];
}

/**
 * What this environment uses from the workspace's other environments (ADR 0026, 0028): its sites'
 * sign-in, another environment's app at an address, operations of an app there, or its Dynamic Apps
 * tables. Each kind is listed as it is, with the way to connect one more.
 */
export function LinksPanel({ env, all }: { env: Environment; all: Environment[] }) {
  const { t } = useTranslation();
  const links = useLinks(env.id);
  const { remove } = useLinkMutations(env.id);
  const [connecting, setConnecting] = useState<Kind | null>(null);
  const nameOf = (id: string) => all.find((e) => e.id === id)?.name ?? id;
  const others = all.filter((e) => e.id !== env.id && e.ready !== false);

  if (links.isLoading) return <CenteredSpinner />;
  if (links.isError) return <LoadFailed onRetry={() => void links.refetch()} />;
  const list = links.data ?? [];
  if (others.length === 0) return <p className="text-sm text-muted-foreground">{t('environments.connect.noOthers')}</p>;

  const describe = (l: EnvironmentLink) => {
    const there = nameOf(l.targetEnvironmentId);
    switch (l.kind) {
      case 'users':
        return t('environments.connect.rows.users', { name: there });
      case 'app':
        return t('environments.connect.rows.app', { address: l.targetKey, name: there });
      default:
        return t(`environments.connect.rows.${l.kind}`, { app: l.targetKey, name: there });
    }
  };

  return (
    <div className="flex flex-col gap-6">
      {KINDS.map((kind) => {
        const Icon = ICONS[kind];
        const rows = list.filter((l) => l.kind === kind);
        // One environment's sign-in comes from one place.
        const canAdd = kind !== 'users' || rows.length === 0;
        return (
          <section key={kind} className="flex flex-col gap-3">
            <SectionHeader
              title={t(`environments.connect.kinds.${kind}.title`)}
              description={t(`environments.connect.kinds.${kind}.description`)}
              actions={
                canAdd ? (
                  <Button variant="outline" size="sm" onClick={() => setConnecting(kind)}>
                    <Icon className="h-4 w-4" /> {t(`environments.connect.kinds.${kind}.action`)}
                  </Button>
                ) : undefined
              }
            />
            {rows.length === 0 ? (
              <p className="text-sm text-muted-foreground">{t(`environments.connect.kinds.${kind}.none`)}</p>
            ) : (
              <Card>
                <CardContent className="divide-y p-0">
                  {rows.map((l) => (
                    <div key={l.id} className="flex items-start gap-3 p-3 text-sm">
                      <Icon className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
                      <div className="min-w-0 flex-1 space-y-1.5">
                        <p className="[overflow-wrap:anywhere]">{describe(l)}</p>
                        {l.kind === 'app' ? <Badge tone="outline">{t(`environments.access.${l.access}`)}</Badge> : null}
                        {l.kind === 'contracts' ? (
                          <div className="flex flex-wrap gap-1">
                            {l.grants.map((g) => (
                              <Badge key={g} tone="outline" className="font-mono">
                                {g}
                              </Badge>
                            ))}
                          </div>
                        ) : null}
                        {l.kind === 'tables' ? (
                          <ul className="space-y-0.5 text-xs text-muted-foreground">
                            {byTable(l.grants).map(([table, verbs]) => (
                              <li key={table}>
                                <span className="font-mono text-foreground">{table}</span>:{' '}
                                {verbs.map((v) => t(`environments.verbs.${v}`, { defaultValue: v })).join(', ')}
                              </li>
                            ))}
                          </ul>
                        ) : null}
                      </div>
                      <Button
                        variant="ghost"
                        size="icon"
                        aria-label={t('environments.connect.remove', { what: describe(l) })}
                        onClick={() => remove.mutate(l.id, { onError: (err) => toastApiError(err, t) })}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  ))}
                </CardContent>
              </Card>
            )}
          </section>
        );
      })}
      {connecting ? (
        <ConnectDialog env={env} others={others} kind={connecting} existing={list} onClose={() => setConnecting(null)} />
      ) : null}
    </div>
  );
}

/**
 * Connecting one thing from another environment: which environment, then what of it, picked
 * from what that environment really has -- its apps by address, the operations they offer, their
 * tables -- with what this environment already has or uses ruled out and said why.
 */
function ConnectDialog({
  env,
  others,
  kind,
  existing,
  onClose,
}: {
  env: Environment;
  others: Environment[];
  kind: Kind;
  existing: EnvironmentLink[];
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { add } = useLinkMutations(env.id);
  const [target, setTarget] = useState<string | null>(others.length === 1 ? others[0].id : null);
  const [app, setApp] = useState<string | null>(null);
  const [access, setAccess] = useState<'read' | 'readWrite'>('read');
  const [grants, setGrants] = useState<string[]>([]);
  const offers = useOffers(target ?? '');
  const own = useOffers(env.id);
  const targetName = others.find((e) => e.id === target)?.name ?? '';
  const apps = offers.data ?? [];
  const chosen = apps.find((a) => a.slug === app);
  const toggle = (grant: string, on: boolean) => setGrants((g) => (on ? [...g, grant] : g.filter((x) => x !== grant)));

  const linked = (a: OfferedApp) =>
    existing.some((l) => l.kind === kind && l.targetEnvironmentId === target && l.targetKey === a.slug)
    || (kind === 'app' && existing.some((l) => l.kind === 'app' && l.targetKey === a.slug));
  const candidates = apps.filter((a) =>
    kind === 'app' ? a.pluginId !== USER_AUTH : kind === 'contracts' ? a.operations.length > 0 : kind === 'tables' ? a.tables.length > 0 : false,
  );
  const hasUsers = apps.some((a) => a.pluginId === USER_AUTH);

  const ready =
    !!target &&
    (kind === 'users' ? hasUsers : !!chosen && (kind === 'app' || grants.length > 0));
  const connect = () => {
    if (!target) return;
    const body =
      kind === 'users'
        ? { kind, targetEnvironmentId: target }
        : kind === 'app'
          ? { kind, targetEnvironmentId: target, targetKey: app!, access }
          : { kind, targetEnvironmentId: target, targetKey: app!, grants };
    add.mutate(body, { onSuccess: onClose, onError: (err) => toastApiError(err, t) });
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent wide className="max-h-[90dvh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{t(`environments.connect.kinds.${kind}.action`)}</DialogTitle>
          <DialogDescription>{t(`environments.connect.kinds.${kind}.description`)}</DialogDescription>
        </DialogHeader>
        <DialogBody className="space-y-5">
          <div className="space-y-2">
            <p className="text-sm font-medium">{t('environments.connect.fromWhich')}</p>
            <ChoiceList<string>
              label={t('environments.connect.fromWhich')}
              value={target}
              columns={2}
              onChange={(id) => {
                setTarget(id);
                setApp(null);
                setGrants([]);
              }}
              choices={others.map((e) => ({
                value: e.id,
                title: e.name,
                description: t(`environments.purposes.${e.isProduction ? 'production' : e.purpose}`),
              }))}
            />
          </div>

          {target && offers.isLoading ? <CenteredSpinner /> : null}
          {target && offers.isError ? <LoadFailed onRetry={() => void offers.refetch()} /> : null}

          {target && offers.data && kind === 'users' ? (
            hasUsers ? (
              <Callout>{t('environments.connect.usersSummary', { here: env.name, name: targetName })}</Callout>
            ) : (
              <Callout tone="warning">{t('environments.connect.noUsers', { name: targetName })}</Callout>
            )
          ) : null}

          {target && offers.data && kind !== 'users' ? (
            candidates.length === 0 ? (
              <Callout tone="warning">{t(`environments.connect.kinds.${kind}.nothingThere`, { name: targetName })}</Callout>
            ) : (
              <div className="space-y-2">
                <p className="text-sm font-medium">{t('environments.connect.whichApp', { name: targetName })}</p>
                <ChoiceList<string>
                  label={t('environments.connect.whichApp', { name: targetName })}
                  value={app}
                  columns={2}
                  onChange={(slug) => {
                    setApp(slug);
                    setGrants([]);
                  }}
                  choices={candidates.map((a) => {
                    const clash = kind === 'app' && (own.data ?? []).some((o) => o.slug === a.slug);
                    return {
                      value: a.slug,
                      title: a.name,
                      description: kind === 'app' ? `/api/${a.slug}` : a.slug,
                      disabledReason: linked(a)
                        ? t('environments.connect.alreadyLinked')
                        : clash
                          ? t('environments.connect.ownApp', { address: `/api/${a.slug}` })
                          : undefined,
                    };
                  })}
                />
              </div>
            )
          ) : null}

          {chosen && kind === 'app' ? (
            <div className="space-y-2">
              <p className="text-sm font-medium">{t('environments.linkAccess')}</p>
              <ChoiceList<'read' | 'readWrite'>
                label={t('environments.linkAccess')}
                value={access}
                columns={2}
                onChange={setAccess}
                choices={[
                  { value: 'read', title: t('environments.access.Read'), description: t('environments.connect.readHint') },
                  { value: 'readWrite', title: t('environments.access.ReadWrite'), description: t('environments.connect.readWriteHint') },
                ]}
              />
              <p className="text-xs text-muted-foreground">
                {t('environments.connect.appSummary', { here: env.name, address: `/api/${chosen.slug}`, name: targetName })}
              </p>
            </div>
          ) : null}

          {chosen && kind === 'contracts' ? (
            <fieldset className="space-y-2">
              <legend className="mb-2 text-sm font-medium">{t('environments.connect.whichOperations')}</legend>
              {chosen.operations.map((o) => (
                <label key={o.grant} className="flex items-start gap-2 rounded-md border p-2.5 text-sm">
                  <Checkbox className="mt-0.5" checked={grants.includes(o.grant)} onCheckedChange={(on) => toggle(o.grant, on === true)} />
                  <span className="min-w-0 flex-1">
                    <span className="font-mono text-xs [overflow-wrap:anywhere]">{o.grant}</span>
                    {o.risk !== 'read' ? (
                      <Badge tone="warning" className="ml-2">
                        {t('environments.useWrites')}
                      </Badge>
                    ) : null}
                    {o.description ? <span className="block text-xs text-muted-foreground">{o.description}</span> : null}
                  </span>
                </label>
              ))}
            </fieldset>
          ) : null}

          {chosen && kind === 'tables' ? (
            <fieldset className="space-y-2">
              <legend className="mb-2 text-sm font-medium">{t('environments.connect.whichTables')}</legend>
              <div className="overflow-x-auto rounded-md border">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b text-xs text-muted-foreground">
                      <th className="p-2 text-left font-medium">{t('environments.connect.table')}</th>
                      {VERBS.map((v) => (
                        <th key={v} className="p-2 font-medium">
                          {t(`environments.verbs.${v}`)}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {chosen.tables.map((table) => (
                      <tr key={table} className="border-b last:border-0">
                        <td className="p-2 font-mono text-xs [overflow-wrap:anywhere]">{table}</td>
                        {VERBS.map((v) => (
                          <td key={v} className="p-2 text-center">
                            <Checkbox
                              aria-label={`${table}:${v}`}
                              checked={grants.includes(`${table}:${v}`)}
                              onCheckedChange={(on) => toggle(`${table}:${v}`, on === true)}
                            />
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </fieldset>
          ) : null}
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button disabled={!ready || add.isPending} onClick={connect}>
            {t(`environments.connect.kinds.${kind}.submit`)}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
