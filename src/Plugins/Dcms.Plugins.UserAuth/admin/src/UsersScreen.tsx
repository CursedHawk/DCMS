import { Ban, LogOut, Mail, MoreHorizontal, Plus, Search, Trash2, UserCheck, Users } from 'lucide-react';
import { useState } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  type Column,
  CopyButton,
  DataTable,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  EmptyState,
  Input,
  Label,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from '@dcms/ui';
import { useCan, usePluginApi, usePluginT } from '@dcms/plugin-ui';
import { type Group, type SiteUser, displayName, useGroups, usePath, useUsers, useWrite } from './api';

/**
 * The people who can sign in to the tenant's sites, and their groups (manifest screen "users").
 * The accounts live in identity's realm for this tenant; every action here goes through the
 * plugin's routes, which check the member's permission and pass it on.
 */
export function UsersScreen() {
  const { t } = usePluginT();
  return (
    <Tabs defaultValue="users" className="space-y-4">
      <TabsList>
        <TabsTrigger value="users">{t('users.tab')}</TabsTrigger>
        <TabsTrigger value="groups">{t('groups.tab')}</TabsTrigger>
      </TabsList>
      <TabsContent value="users">
        <UsersTab />
      </TabsContent>
      <TabsContent value="groups">
        <GroupsTab />
      </TabsContent>
    </Tabs>
  );
}

function UsersTab() {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const canManage = useCan('users-manage');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const users = useUsers(search, page);
  const groups = useGroups();
  const [inviting, setInviting] = useState(false);
  const [editingGroups, setEditingGroups] = useState<SiteUser | null>(null);

  const groupName = (id: string) => groups.data?.find((g) => g.id === id)?.name ?? t('groups.unknown');
  const setStatus = useWrite((args: { user: SiteUser; status: 'active' | 'disabled' }) =>
    api.patch(path(`/users/${args.user.id}`), { status: args.status }));
  const reinvite = useWrite((user: SiteUser) => api.post(path(`/users/${user.id}/invite`)));
  const signOut = useWrite((user: SiteUser) => api.post(path(`/users/${user.id}/sign-out`)));
  const remove = useWrite((user: SiteUser) => api.del(path(`/users/${user.id}`)));

  const columns: Column<SiteUser>[] = [
    {
      id: 'name',
      header: t('users.columns.name'),
      primary: true,
      sortValue: (u) => displayName(u).toLowerCase(),
      cell: (u) => (
        <div className="min-w-0">
          <div className="truncate font-medium">{displayName(u)}</div>
          {u.displayName ? <div className="truncate text-xs text-muted-foreground">{u.email}</div> : null}
        </div>
      ),
    },
    {
      id: 'status',
      header: t('users.columns.status'),
      width: '8rem',
      cell: (u) => (
        <Badge tone={u.status === 'active' ? 'success' : u.status === 'invited' ? 'warning' : 'secondary'}>
          {u.lockedOut ? t('users.status.lockedOut') : t(`users.status.${u.status}`)}
        </Badge>
      ),
    },
    {
      id: 'groups',
      header: t('users.columns.groups'),
      cell: (u) =>
        u.groups.length === 0 ? (
          <span className="text-muted-foreground">—</span>
        ) : (
          <div className="flex flex-wrap gap-1">
            {u.groups.map((g) => (
              <Badge key={g} tone="outline">
                {groupName(g)}
              </Badge>
            ))}
          </div>
        ),
    },
    {
      id: 'lastSignIn',
      header: t('users.columns.lastSignIn'),
      width: '11rem',
      sortValue: (u) => u.lastSignInAt,
      cell: (u) => (u.lastSignInAt ? new Date(u.lastSignInAt).toLocaleString() : <span className="text-muted-foreground">{t('users.never')}</span>),
    },
    {
      id: 'actions',
      header: '',
      srHeader: t('common.actions', { defaultValue: 'Actions' }),
      width: '3rem',
      align: 'right',
      hideOnCard: !canManage,
      cell: (u) =>
        canManage ? (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" aria-label={t('users.actions.menu')}>
                <MoreHorizontal className="size-4" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem onSelect={() => setEditingGroups(u)}>
                <Users className="size-4" /> {t('users.actions.groups')}
              </DropdownMenuItem>
              {u.status === 'invited' ? (
                <DropdownMenuItem onSelect={() => reinvite.mutate(u)}>
                  <Mail className="size-4" /> {t('users.actions.reinvite')}
                </DropdownMenuItem>
              ) : null}
              {u.status === 'disabled' ? (
                <DropdownMenuItem onSelect={() => setStatus.mutate({ user: u, status: 'active' })}>
                  <UserCheck className="size-4" /> {t('users.actions.enable')}
                </DropdownMenuItem>
              ) : u.status === 'active' ? (
                <DropdownMenuItem onSelect={() => setStatus.mutate({ user: u, status: 'disabled' })}>
                  <Ban className="size-4" /> {t('users.actions.disable')}
                </DropdownMenuItem>
              ) : null}
              <DropdownMenuItem onSelect={() => signOut.mutate(u)}>
                <LogOut className="size-4" /> {t('users.actions.signOut')}
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem
                className="text-destructive"
                onSelect={() => {
                  if (window.confirm(t('users.confirmDelete', { email: u.email }))) remove.mutate(u);
                }}
              >
                <Trash2 className="size-4" /> {t('users.actions.delete')}
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        ) : null,
    },
  ];

  const total = users.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / 50));

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-56 flex-1">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
          <Input
            className="pl-8"
            placeholder={t('users.search')}
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setPage(1);
            }}
          />
        </div>
        {canManage ? (
          <Button onClick={() => setInviting(true)}>
            <Plus className="size-4" /> {t('users.invite')}
          </Button>
        ) : null}
      </div>
      <DataTable
        rows={users.data?.items}
        columns={columns}
        rowKey={(u) => u.id}
        isLoading={users.isLoading}
        error={users.isError ? t('errors.load') : undefined}
        empty={<EmptyState icon={Users} title={t('users.empty.title')} description={t('users.empty.description')} />}
      />
      {pages > 1 ? (
        <div className="flex items-center justify-end gap-2 text-sm">
          <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>
            {t('pager.previous')}
          </Button>
          <span className="text-muted-foreground">{t('pager.page', { page, pages })}</span>
          <Button variant="outline" size="sm" disabled={page >= pages} onClick={() => setPage(page + 1)}>
            {t('pager.next')}
          </Button>
        </div>
      ) : null}
      <InviteDialog open={inviting} onOpenChange={setInviting} groups={groups.data ?? []} />
      {editingGroups ? (
        <MembershipDialog user={editingGroups} groups={groups.data ?? []} onClose={() => setEditingGroups(null)} />
      ) : null}
    </div>
  );
}

function InviteDialog({ open, onOpenChange, groups }: { open: boolean; onOpenChange: (open: boolean) => void; groups: Group[] }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  const [chosen, setChosen] = useState<string[]>([]);
  const [link, setLink] = useState<string | null>(null);
  const invite = useWrite(async () => {
    const result = await api.post<{ inviteUrl: string }>(path('/users/invite'), {
      email: email.trim(),
      displayName: name.trim() || null,
      groups: chosen,
    });
    setLink(result.inviteUrl);
  });

  const close = (next: boolean) => {
    onOpenChange(next);
    if (!next) {
      setEmail('');
      setName('');
      setChosen([]);
      setLink(null);
    }
  };

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t('invite.title')}</DialogTitle>
        </DialogHeader>
        {link ? (
          <DialogBody className="space-y-3 text-sm">
            <p>{t('invite.sent', { email })}</p>
            <p className="text-muted-foreground">{t('invite.linkHint')}</p>
            <div className="flex items-center gap-2 rounded-md border bg-muted/40 p-2">
              <code className="min-w-0 flex-1 truncate text-xs">{link}</code>
              <CopyButton value={link} />
            </div>
          </DialogBody>
        ) : (
          <DialogBody className="space-y-4">
            <div className="space-y-1.5">
              <Label htmlFor="ua-invite-email">{t('invite.email')}</Label>
              <Input id="ua-invite-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="ua-invite-name">{t('invite.name')}</Label>
              <Input id="ua-invite-name" value={name} onChange={(e) => setName(e.target.value)} />
            </div>
            {groups.length > 0 ? (
              <fieldset className="space-y-2">
                <legend className="text-sm font-medium">{t('invite.groups')}</legend>
                <GroupChecklist groups={groups} value={chosen} onChange={setChosen} />
              </fieldset>
            ) : null}
          </DialogBody>
        )}
        <DialogFooter>
          {link ? (
            <Button onClick={() => close(false)}>{t('common.done', { defaultValue: 'Done' })}</Button>
          ) : (
            <>
              <Button variant="outline" onClick={() => close(false)}>
                {t('common.cancel', { defaultValue: 'Cancel' })}
              </Button>
              <Button disabled={!email.includes('@') || invite.isPending} onClick={() => invite.mutate(undefined)}>
                <Mail className="size-4" /> {t('invite.send')}
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** A user's groups, changed one membership at a time (identity's API is per membership). */
function MembershipDialog({ user, groups, onClose }: { user: SiteUser; groups: Group[]; onClose: () => void }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const [value, setValue] = useState(user.groups);
  const save = useWrite(async () => {
    for (const g of value.filter((id) => !user.groups.includes(id))) await api.put(path(`/groups/${g}/members/${user.id}`));
    for (const g of user.groups.filter((id) => !value.includes(id))) await api.del(path(`/groups/${g}/members/${user.id}`));
  }, onClose);
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t('membership.title', { name: displayName(user) })}</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-3">
          {groups.length === 0 ? <p className="text-sm text-muted-foreground">{t('membership.noGroups')}</p> : null}
          <GroupChecklist groups={groups} value={value} onChange={setValue} />
          <p className="text-xs text-muted-foreground">{t('membership.hint')}</p>
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('common.cancel', { defaultValue: 'Cancel' })}
          </Button>
          <Button disabled={save.isPending} onClick={() => save.mutate(undefined)}>
            {t('common.save', { defaultValue: 'Save' })}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

export function GroupChecklist({ groups, value, onChange }: { groups: Group[]; value: string[]; onChange: (value: string[]) => void }) {
  return (
    <div className="grid gap-2 sm:grid-cols-2">
      {groups.map((g) => (
        <label key={g.id} className="flex items-center gap-2 text-sm">
          <Checkbox
            checked={value.includes(g.id)}
            onCheckedChange={(checked) => onChange(checked ? [...value, g.id] : value.filter((id) => id !== g.id))}
          />
          {g.name}
        </label>
      ))}
    </div>
  );
}

function GroupsTab() {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const canManage = useCan('users-manage');
  const groups = useGroups();
  const [editing, setEditing] = useState<Group | 'new' | null>(null);
  const remove = useWrite((g: Group) => api.del(path(`/groups/${g.id}`)));

  const columns: Column<Group>[] = [
    {
      id: 'name',
      header: t('groups.columns.name'),
      primary: true,
      sortValue: (g) => g.name.toLowerCase(),
      cell: (g) => (
        <div className="min-w-0">
          <div className="truncate font-medium">{g.name}</div>
          {g.description ? <div className="truncate text-xs text-muted-foreground">{g.description}</div> : null}
        </div>
      ),
    },
    { id: 'members', header: t('groups.columns.members'), width: '8rem', align: 'right', sortValue: (g) => g.members, cell: (g) => g.members },
    {
      id: 'actions',
      header: '',
      srHeader: t('common.actions', { defaultValue: 'Actions' }),
      width: '6rem',
      align: 'right',
      hideOnCard: !canManage,
      cell: (g) =>
        canManage ? (
          <div className="flex justify-end gap-1">
            <Button variant="ghost" size="sm" onClick={() => setEditing(g)}>
              {t('groups.rename')}
            </Button>
            <Button
              variant="ghost"
              size="icon"
              aria-label={t('groups.delete')}
              onClick={() => {
                if (window.confirm(t('groups.confirmDelete', { name: g.name }))) remove.mutate(g);
              }}
            >
              <Trash2 className="size-4" />
            </Button>
          </div>
        ) : null,
    },
  ];

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">{t('groups.intro')}</p>
        {canManage ? (
          <Button onClick={() => setEditing('new')}>
            <Plus className="size-4" /> {t('groups.create')}
          </Button>
        ) : null}
      </div>
      <DataTable
        rows={groups.data}
        columns={columns}
        rowKey={(g) => g.id}
        isLoading={groups.isLoading}
        error={groups.isError ? t('errors.load') : undefined}
        empty={<EmptyState icon={Users} title={t('groups.empty.title')} description={t('groups.empty.description')} />}
      />
      {editing ? <GroupDialog group={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </div>
  );
}

function GroupDialog({ group, onClose }: { group: Group | null; onClose: () => void }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const [name, setName] = useState(group?.name ?? '');
  const [description, setDescription] = useState(group?.description ?? '');
  const save = useWrite(() => {
    const body = { name: name.trim(), description: description.trim() || null };
    return group ? api.put(path(`/groups/${group.id}`), body) : api.post(path('/groups'), body);
  }, onClose);
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{group ? t('groups.renameTitle') : t('groups.createTitle')}</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-4">
          <div className="space-y-1.5">
            <Label htmlFor="ua-group-name">{t('groups.columns.name')}</Label>
            <Input id="ua-group-name" value={name} onChange={(e) => setName(e.target.value)} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="ua-group-description">{t('groups.description')}</Label>
            <Input id="ua-group-description" value={description} onChange={(e) => setDescription(e.target.value)} />
          </div>
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('common.cancel', { defaultValue: 'Cancel' })}
          </Button>
          <Button disabled={!name.trim() || save.isPending} onClick={() => save.mutate(undefined)}>
            {t('common.save', { defaultValue: 'Save' })}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
