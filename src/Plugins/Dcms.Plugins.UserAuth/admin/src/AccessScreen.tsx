import { AlertTriangle, ArrowDown, ArrowUp, Globe, KeyRound, Lock, Plus, ShieldCheck, Trash2, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Checkbox,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  TagsInput,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from '@dcms/ui';
import { useCan, usePluginApi, usePluginT } from '@dcms/plugin-ui';
import {
  type Access,
  type ApiAccess,
  type Group,
  type InstanceApiAccess,
  type Role,
  type Rule,
  displayName,
  useApiAccess,
  useGroups,
  usePath,
  useResources,
  useRoles,
  useSites,
  useUsers,
  useWrite,
} from './api';
import { assetsConflict, instancesBehind, withFirst } from './ruleChecks';
import { GroupChecklist } from './UsersScreen';

/**
 * Who may reach what (manifest screen "access"): each site's path rules, which the edge enforces
 * before a page is served; each plugin instance's API access, which content-api enforces on its
 * data whatever page asked; and roles — bundles of site permissions given to groups or users.
 */
export function AccessScreen() {
  const { t } = usePluginT();
  return (
    <Tabs defaultValue="sites" className="space-y-4">
      <TabsList>
        <TabsTrigger value="sites">{t('sites.tab')}</TabsTrigger>
        <TabsTrigger value="api">{t('api.tab')}</TabsTrigger>
        <TabsTrigger value="roles">{t('roles.tab')}</TabsTrigger>
      </TabsList>
      <TabsContent value="sites">
        <SitesTab />
      </TabsContent>
      <TabsContent value="api">
        <ApiTab />
      </TabsContent>
      <TabsContent value="roles">
        <RolesTab />
      </TabsContent>
    </Tabs>
  );
}

// ---- site rules --------------------------------------------------------------------------------

function SitesTab() {
  const { t } = usePluginT();
  const sites = useSites();
  const groups = useGroups();
  const [siteId, setSiteId] = useState<string | null>(null);
  useEffect(() => {
    if (!siteId && sites.data?.[0]) setSiteId(sites.data[0].id);
  }, [sites.data, siteId]);
  const site = sites.data?.find((s) => s.id === siteId);

  if (sites.isLoading) return null;
  if (!sites.data?.length) {
    return <EmptyState icon={Globe} title={t('sites.empty.title')} description={t('sites.empty.description')} />;
  }
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <Select value={siteId ?? undefined} onValueChange={setSiteId}>
          <SelectTrigger className="w-64">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {sites.data.map((s) => (
              <SelectItem key={s.id} value={s.id}>
                {s.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <div className="flex flex-wrap gap-1">
          {site?.hosts.length ? (
            site.hosts.map((h) => (
              <Badge key={h.hostname} tone={h.verified ? 'outline' : 'warning'}>
                {h.hostname}
                {h.verified ? '' : ` · ${t('sites.unverified')}`}
              </Badge>
            ))
          ) : (
            <span className="text-sm text-muted-foreground">{t('sites.noHosts')}</span>
          )}
        </div>
      </div>
      {site ? <RulesEditor key={site.id} siteId={site.id} spa={site.spa} saved={site.rules} groups={groups.data ?? []} /> : null}
    </div>
  );
}

function RulesEditor({ siteId, spa, saved, groups }: { siteId: string; spa: boolean; saved: Rule[]; groups: Group[] }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const canEdit = useCan('access-manage');
  const [rules, setRules] = useState<Rule[]>(saved);
  const dirty = JSON.stringify(rules) !== JSON.stringify(saved);
  const save = useWrite(() => api.put(path(`/sites/${siteId}/rules`), rules));

  const update = (i: number, patch: Partial<Rule>) => setRules(rules.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const move = (i: number, by: number) => {
    const next = [...rules];
    [next[i], next[i + by]] = [next[i + by]!, next[i]!];
    setRules(next);
  };

  return (
    <Card className="space-y-4 p-4">
      <p className="text-sm text-muted-foreground">{t('sites.intro')}</p>
      {spa ? <SpaNotice rules={rules} canEdit={canEdit} onFix={setRules} /> : null}
      {rules.length === 0 ? <p className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">{t('sites.noRules')}</p> : null}
      <ol className="space-y-3">
        {rules.map((rule, i) => (
          <li key={i} className="space-y-2 rounded-md border p-3">
            <div className="flex flex-wrap items-center gap-2">
              <span className="w-6 text-sm tabular-nums text-muted-foreground">{i + 1}.</span>
              <Input
                className="w-64 font-mono"
                aria-label={t('sites.prefix')}
                value={rule.prefix}
                disabled={!canEdit}
                onChange={(e) => update(i, { prefix: e.target.value })}
              />
              <Select value={rule.access} disabled={!canEdit} onValueChange={(access) => update(i, { access: access as Access })}>
                <SelectTrigger className="w-56">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {(['public', 'signedIn', 'groups'] as const).map((a) => (
                    <SelectItem key={a} value={a}>
                      {t(`sites.access.${a}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              {canEdit ? (
                <div className="ml-auto flex gap-1">
                  <Button variant="ghost" size="icon" disabled={i === 0} aria-label={t('sites.up')} onClick={() => move(i, -1)}>
                    <ArrowUp className="size-4" />
                  </Button>
                  <Button variant="ghost" size="icon" disabled={i === rules.length - 1} aria-label={t('sites.down')} onClick={() => move(i, 1)}>
                    <ArrowDown className="size-4" />
                  </Button>
                  <Button variant="ghost" size="icon" aria-label={t('sites.remove')} onClick={() => setRules(rules.filter((_, j) => j !== i))}>
                    <Trash2 className="size-4" />
                  </Button>
                </div>
              ) : null}
            </div>
            {rule.access === 'groups' ? (
              <div className="pl-8">
                {groups.length ? (
                  <GroupChecklist groups={groups} value={rule.groups} onChange={(g) => update(i, { groups: g })} />
                ) : (
                  <p className="text-sm text-muted-foreground">{t('sites.noGroups')}</p>
                )}
              </div>
            ) : null}
          </li>
        ))}
      </ol>
      {canEdit ? (
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="outline" onClick={() => setRules([...rules, { prefix: '/', access: 'signedIn', groups: [] }])}>
            <Plus className="size-4" /> {t('sites.add')}
          </Button>
          <div className="ml-auto flex gap-2">
            <Button variant="ghost" disabled={!dirty} onClick={() => setRules(saved)}>
              {t('common.discard', { defaultValue: 'Discard' })}
            </Button>
            <Button disabled={!dirty || save.isPending} onClick={() => save.mutate(undefined)}>
              {t('common.save', { defaultValue: 'Save' })}
            </Button>
          </div>
        </div>
      ) : null}
    </Card>
  );
}

/**
 * A single-page app's caveats, where the rules are edited: they guard direct loads, not the app's
 * own navigation, so its data needs API access too; and its code is one bundle under /assets.
 */
function SpaNotice({ rules, canEdit, onFix }: { rules: Rule[]; canEdit: boolean; onFix: (rules: Rule[]) => void }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const instances = useApiAccess();
  const restrict = useWrite((i: InstanceApiAccess) => api.put(path(`/api-access/${i.instanceId}`), { access: 'signedIn' }));
  const behind = instancesBehind(rules, instances.data ?? []);
  const conflict = assetsConflict(rules);
  return (
    <div className="space-y-2">
      <div className="space-y-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-3 text-sm">
        <p className="flex items-start gap-2">
          <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden /> {t('sites.spa.notice')}
        </p>
        {behind.map((i) => (
          <div key={i.instanceId} className="flex flex-wrap items-center gap-2 pl-6">
            <span>{t('sites.spa.instance', { name: i.name, slug: i.slug })}</span>
            <Badge tone={i.access === 'public' ? 'warning' : 'outline'}>{t(`api.access.${i.access}`)}</Badge>
            {canEdit && i.access === 'public' ? (
              <Button size="sm" variant="outline" disabled={restrict.isPending} onClick={() => restrict.mutate(i)}>
                {t('sites.spa.restrict')}
              </Button>
            ) : null}
          </div>
        ))}
      </div>
      {conflict ? (
        <div className="space-y-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-3 text-sm">
          <p className="flex items-start gap-2">
            <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden />
            {t('sites.spa.assets', { paths: conflict.affected.map((r) => r.prefix).join(', ') })}
          </p>
          {canEdit ? (
            <Button size="sm" variant="outline" className="ml-6" onClick={() => onFix(withFirst(rules, conflict.fix))}>
              {t('sites.spa.assetsFix', { access: t(`sites.access.${conflict.fix.access}`) })}
            </Button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

// ---- API access --------------------------------------------------------------------------------

function ApiTab() {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const canEdit = useCan('access-manage');
  const instances = useApiAccess();
  const set = useWrite((args: { instance: InstanceApiAccess; access: ApiAccess }) =>
    api.put(path(`/api-access/${args.instance.instanceId}`), { access: args.access }));

  if (instances.isLoading) return null;
  return (
    <Card className="space-y-4 p-4">
      <p className="text-sm text-muted-foreground">{t('api.intro')}</p>
      {instances.data?.length === 0 ? <p className="text-sm text-muted-foreground">{t('api.empty')}</p> : null}
      <ul className="divide-y">
        {instances.data?.map((i) => (
          <li key={i.instanceId} className="flex flex-wrap items-center gap-3 py-3">
            <div className="min-w-48 flex-1">
              <div className="font-medium">{i.name}</div>
              <div className="font-mono text-xs text-muted-foreground">/api/{i.slug}</div>
            </div>
            <Select
              value={i.access}
              disabled={!canEdit || set.isPending}
              onValueChange={(access) => set.mutate({ instance: i, access: access as ApiAccess })}
            >
              <SelectTrigger className="w-64" aria-label={t('api.accessFor', { name: i.name })}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {(['public', 'signedIn', 'permission'] as const).map((a) => (
                  <SelectItem key={a} value={a}>
                    {t(`api.access.${a}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {i.access === 'permission' ? (
              <p className="basis-full text-xs text-muted-foreground">
                {t('api.permissions')} <code className="font-mono">{i.readPermission}</code> · <code className="font-mono">{i.writePermission}</code>
              </p>
            ) : null}
          </li>
        ))}
      </ul>
    </Card>
  );
}

// ---- roles -------------------------------------------------------------------------------------

function RolesTab() {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const canEdit = useCan('access-manage');
  const roles = useRoles();
  const groups = useGroups();
  const users = useUsers('', 1);
  const [editing, setEditing] = useState<Role | 'new' | null>(null);
  const remove = useWrite((role: Role) => api.del(path(`/roles/${role.id}`)));
  const revoke = useWrite((args: { role: Role; grant: string }) => api.del(path(`/roles/${args.role.id}/grants/${args.grant}`)));
  const grant = useWrite((args: { role: Role; subjectType: 'group' | 'user'; subjectId: string }) =>
    api.post(path(`/roles/${args.role.id}/grants`), { subjectType: args.subjectType, subjectId: args.subjectId }));

  const subjectName = (type: 'group' | 'user', id: string) => {
    if (type === 'group') return groups.data?.find((g) => g.id === id)?.name ?? t('groups.unknown');
    const user = users.data?.items.find((u) => u.id === id);
    return user ? displayName(user) : t('roles.unknownUser');
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">{t('roles.intro')}</p>
        {canEdit ? (
          <Button onClick={() => setEditing('new')}>
            <Plus className="size-4" /> {t('roles.create')}
          </Button>
        ) : null}
      </div>
      {roles.data?.length === 0 ? <EmptyState icon={ShieldCheck} title={t('roles.empty.title')} description={t('roles.empty.description')} /> : null}
      <div className="grid gap-3 lg:grid-cols-2">
        {roles.data?.map((role) => (
          <Card key={role.id} className="space-y-3 p-4">
            <div className="flex items-start justify-between gap-2">
              <div className="min-w-0">
                <div className="font-medium">{role.name}</div>
                <div className="font-mono text-xs text-muted-foreground">{role.key}</div>
                {role.description ? <p className="mt-1 text-sm text-muted-foreground">{role.description}</p> : null}
              </div>
              {canEdit ? (
                <div className="flex shrink-0 gap-1">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(role)}>
                    {t('roles.edit')}
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label={t('roles.delete')}
                    onClick={() => {
                      if (window.confirm(t('roles.confirmDelete', { name: role.name }))) remove.mutate(role);
                    }}
                  >
                    <Trash2 className="size-4" />
                  </Button>
                </div>
              ) : null}
            </div>
            <div className="text-sm">
              <KeyRound className="mr-1 inline size-3.5 text-muted-foreground" aria-hidden />
              {t('roles.permissionCount', { count: role.permissions.length })}
            </div>
            <div className="space-y-1.5">
              <div className="text-xs font-medium uppercase text-muted-foreground">{t('roles.heldBy')}</div>
              <div className="flex flex-wrap gap-1">
                {role.grants.length === 0 ? <span className="text-sm text-muted-foreground">{t('roles.nobody')}</span> : null}
                {role.grants.map((g) => (
                  <Badge key={g.id} tone={g.subjectType === 'group' ? 'default' : 'outline'} className="gap-1">
                    {subjectName(g.subjectType, g.subjectId)}
                    {canEdit ? (
                      <button type="button" aria-label={t('roles.revoke')} onClick={() => revoke.mutate({ role, grant: g.id })}>
                        <X className="size-3" />
                      </button>
                    ) : null}
                  </Badge>
                ))}
              </div>
              {canEdit ? (
                <Select
                  value=""
                  onValueChange={(value) => {
                    const [subjectType, subjectId] = value.split(':') as ['group' | 'user', string];
                    grant.mutate({ role, subjectType, subjectId });
                  }}
                >
                  <SelectTrigger className="h-8 w-56 text-xs">
                    <SelectValue placeholder={t('roles.grantTo')} />
                  </SelectTrigger>
                  <SelectContent>
                    {groups.data?.map((g) => (
                      <SelectItem key={g.id} value={`group:${g.id}`}>
                        {t('roles.group', { name: g.name })}
                      </SelectItem>
                    ))}
                    {users.data?.items.map((u) => (
                      <SelectItem key={u.id} value={`user:${u.id}`}>
                        {displayName(u)}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              ) : null}
            </div>
          </Card>
        ))}
      </div>
      {editing ? <RoleDialog role={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </div>
  );
}

function RoleDialog({ role, onClose }: { role: Role | null; onClose: () => void }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const resources = useResources();
  const [name, setName] = useState(role?.name ?? '');
  const [key, setKey] = useState(role?.key ?? '');
  const [description, setDescription] = useState(role?.description ?? '');
  const [permissions, setPermissions] = useState<string[]>(role?.permissions ?? []);
  const save = useWrite(() => {
    const body = { key, name: name.trim(), description: description.trim() || null, permissions };
    return role ? api.put(path(`/roles/${role.id}`), body) : api.post(path('/roles'), body);
  }, onClose);

  // Offered by the plugins that gate something (users.resources@1); anything else stays editable as text.
  const offered = useMemo(
    () =>
      new Set(
        (resources.data ?? []).flatMap((c) => c.resources.flatMap((r) => r.actions.map((a) => `${c.plugin}:${c.instance}:${r.resource}:${a.action}`))),
      ),
    [resources.data],
  );
  const toggle = (permission: string, on: boolean) =>
    setPermissions(on ? [...permissions, permission] : permissions.filter((p) => p !== permission));

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-2xl">
        <DialogHeader>
          <DialogTitle>{role ? t('roles.editTitle') : t('roles.createTitle')}</DialogTitle>
        </DialogHeader>
        <DialogBody className="max-h-[70vh] space-y-4 overflow-y-auto">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="ua-role-name">{t('roles.name')}</Label>
              <Input
                id="ua-role-name"
               
                value={name}
                onChange={(e) => {
                  setName(e.target.value);
                  if (!role) setKey(e.target.value.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '').replace(/^(\d)/, 'r_$1').slice(0, 64));
                }}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="ua-role-key">{t('roles.key')}</Label>
              <Input id="ua-role-key" className="font-mono" value={key} onChange={(e) => setKey(e.target.value)} />
            </div>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="ua-role-description">{t('roles.description')}</Label>
            <Input id="ua-role-description" value={description} onChange={(e) => setDescription(e.target.value)} />
          </div>
          <div className="space-y-3">
            <div className="text-sm font-medium">{t('roles.permissions')}</div>
            {(resources.data ?? []).length === 0 ? <p className="text-sm text-muted-foreground">{t('roles.noResources')}</p> : null}
            {resources.data?.map((catalog) => (
              <fieldset key={`${catalog.plugin}:${catalog.instance}`} className="space-y-2 rounded-md border p-3">
                <legend className="px-1 text-sm font-medium">{catalog.label}</legend>
                {catalog.resources.map((r) => (
                  <div key={r.resource} className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
                    <span className="w-40 truncate">{r.label}</span>
                    {r.actions.map((a) => {
                      const permission = `${catalog.plugin}:${catalog.instance}:${r.resource}:${a.action}`;
                      return (
                        <label key={a.action} className="flex items-center gap-1.5">
                          <Checkbox checked={permissions.includes(permission)} onCheckedChange={(on) => toggle(permission, on === true)} />
                          {a.label}
                        </label>
                      );
                    })}
                  </div>
                ))}
              </fieldset>
            ))}
            <div className="space-y-1.5">
              <Label>{t('roles.otherPermissions')}</Label>
              <TagsInput
                value={permissions.filter((p) => !offered.has(p))}
                onChange={(other) => setPermissions([...permissions.filter((p) => offered.has(p)), ...other])}
                placeholder="plugin:instance:resource:action"
              />
            </div>
          </div>
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('common.cancel', { defaultValue: 'Cancel' })}
          </Button>
          <Button disabled={!name.trim() || !key || save.isPending} onClick={() => save.mutate(undefined)}>
            <Lock className="size-4" /> {t('common.save', { defaultValue: 'Save' })}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
