import { useMutation, useQueryClient } from '@tanstack/react-query';
import { MailPlus, Plus, Trash2, UserPlus, Users, X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Checkbox,
  CopyButton,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  type Column,
  DataTable,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  EmptyState,
  Input,
  Label,
  Page,
  PageHeader,
  toastApiError,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { type Invitation, type Member, useInvitations, useMembers, useRoles } from '../rbac/api';

/** Short absolute date, e.g. "19 Aug 2026" in the active locale. */
function formatDate(iso: string, locale: string): string {
  return new Date(iso).toLocaleDateString(locale, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}

export function MembersPage() {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const members = useMembers();
  const roles = useRoles();
  const invitations = useInvitations();
  const [inviteOpen, setInviteOpen] = useState(false);
  const [email, setEmail] = useState('');
  const [inviteRoles, setInviteRoles] = useState<Set<string>>(new Set());
  const [inviteLink, setInviteLink] = useState<string | null>(null);
  const [invitedEmail, setInvitedEmail] = useState('');

  const roleName = (id: string) => roles.data?.find((r) => r.id === id)?.name ?? id;

  const addRole = useMutation({
    mutationFn: ({ membershipId, roleId }: { membershipId: string; roleId: string }) =>
      api.post(`/admin/members/${membershipId}/roles`, { roleId }),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['members'] });
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const removeRole = useMutation({
    mutationFn: ({ membershipId, roleId }: { membershipId: string; roleId: string }) =>
      api.del(`/admin/members/${membershipId}/roles/${roleId}`),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['members'] });
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const invite = useMutation({
    mutationFn: () =>
      api.post<{ link: string; email: string }>('/admin/invitations', {
        email: email.trim(),
        roleIds: [...inviteRoles],
      }),
    onSuccess: async (created) => {
      setInviteLink(created.link);
      setInvitedEmail(created.email);
      toast.success(t('members.inviteSent'));
      await qc.invalidateQueries({ queryKey: ['invitations'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  // Resending mints a fresh token server-side, so the returned link replaces the
  // one in the dialog — an admin who resends then copies must get the live link.
  const resend = useMutation({
    mutationFn: (id: string) => api.post<{ link: string; email: string }>(`/admin/invitations/${id}/resend`, {}),
    onSuccess: async (sent) => {
      setInviteLink(sent.link);
      setInvitedEmail(sent.email);
      setInviteOpen(true);
      toast.success(t('members.resent'));
      await qc.invalidateQueries({ queryKey: ['invitations'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const revoke = useMutation({
    mutationFn: (id: string) => api.del(`/admin/invitations/${id}`),
    onSuccess: async () => {
      toast.success(t('members.revoked'));
      await qc.invalidateQueries({ queryKey: ['invitations'] });
    },
    onError: () => toast.error(t('errors.generic')),
  });

  function resetInvite() {
    setEmail('');
    setInviteRoles(new Set());
    setInviteLink(null);
    setInvitedEmail('');
  }

  const pending = invitations.data ?? [];

  /*
   * Columns are declared here rather than at module scope because every cell needs something
   * from this render: the role name lookup, the mutations, the translation function. Building
   * them per render is fine — DataTable does no memo-sensitive work with them.
   */
  const memberColumns: Column<Member>[] = [
    {
      id: 'email',
      header: t('members.email'),
      primary: true,
      cell: (m) => <span className="font-medium">{m.email}</span>,
      sortValue: (m) => m.email.toLowerCase(),
    },
    {
      id: 'roles',
      header: t('members.roles'),
      cell: (m) => (
        <div className="flex flex-wrap items-center gap-1">
          {m.roleIds.length === 0 ? (
            <span className="text-xs text-muted-foreground">{t('members.noRoles')}</span>
          ) : (
            m.roleIds.map((rid) => (
              <Badge key={rid} tone="secondary" className="gap-1 pr-1">
                {roleName(rid)}
                <button
                  type="button"
                  aria-label={t('members.removeRole', { role: roleName(rid), email: m.email })}
                  className="rounded-full p-0.5 hover:bg-foreground/10"
                  onClick={() => removeRole.mutate({ membershipId: m.membershipId, roleId: rid })}
                >
                  <X className="h-3 w-3" aria-hidden />
                </button>
              </Badge>
            ))
          )}
        </div>
      ),
    },
    {
      id: 'add-role',
      header: '',
      srHeader: t('members.addRole'),
      width: 'w-10',
      align: 'right',
      cell: (m) => {
        const unassigned = (roles.data ?? []).filter((r) => !m.roleIds.includes(r.id));
        if (unassigned.length === 0) return null;
        return (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button size="icon" variant="ghost" aria-label={t('members.addRole')}>
                <Plus className="h-4 w-4" aria-hidden />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              {unassigned.map((r) => (
                <DropdownMenuItem
                  key={r.id}
                  onClick={() => addRole.mutate({ membershipId: m.membershipId, roleId: r.id })}
                >
                  {r.name}
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        );
      },
    },
  ];

  const invitationColumns: Column<Invitation>[] = [
    {
      id: 'email',
      header: t('members.email'),
      primary: true,
      cell: (inv) => <span className="font-medium">{inv.email}</span>,
      sortValue: (inv) => inv.email.toLowerCase(),
    },
    {
      id: 'roles',
      header: t('members.roles'),
      cell: (inv) => (
        <div className="flex flex-wrap items-center gap-1">
          {inv.roleIds.length === 0 ? (
            <span className="text-xs text-muted-foreground">{t('members.noRoles')}</span>
          ) : (
            inv.roleIds.map((rid) => (
              <Badge key={rid} tone="secondary">
                {roleName(rid)}
              </Badge>
            ))
          )}
        </div>
      ),
    },
    {
      id: 'invited',
      header: t('members.invited'),
      cell: (inv) => (
        <span className="text-sm text-muted-foreground">{formatDate(inv.createdAt, i18n.language)}</span>
      ),
      sortValue: (inv) => inv.createdAt,
    },
    {
      id: 'expires',
      header: t('members.expires'),
      cell: (inv) =>
        inv.expired ? (
          <Badge tone="destructive">{t('members.expired')}</Badge>
        ) : (
          <span className="text-sm text-muted-foreground">
            {formatDate(inv.expiresAt, i18n.language)}
          </span>
        ),
      sortValue: (inv) => inv.expiresAt,
    },
    {
      id: 'actions',
      header: '',
      srHeader: t('common.actions'),
      align: 'right',
      width: 'w-20',
      cell: (inv) => (
        <div className="flex items-center justify-end gap-1">
          <Button
            size="icon"
            variant="ghost"
            title={t('members.resend')}
            aria-label={t('members.resend')}
            disabled={resend.isPending}
            onClick={() => resend.mutate(inv.id)}
          >
            <MailPlus className="h-4 w-4" aria-hidden />
          </Button>
          <Button
            size="icon"
            variant="ghost"
            title={t('members.revoke')}
            aria-label={t('members.revoke')}
            disabled={revoke.isPending}
            onClick={() => {
              if (window.confirm(t('members.revokeConfirm', { email: inv.email }))) {
                revoke.mutate(inv.id);
              }
            }}
          >
            <Trash2 className="h-4 w-4" aria-hidden />
          </Button>
        </div>
      ),
    },
  ];

  return (
    <Page>
      <PageHeader
        title={t('members.title')}
        actions={
          <Button
            onClick={() => {
              resetInvite();
              setInviteOpen(true);
            }}
          >
            <UserPlus className="h-4 w-4" /> {t('members.invite')}
          </Button>
        }
      />

      <DataTable
        rows={members.data}
        columns={memberColumns}
        rowKey={(m) => m.membershipId}
        isLoading={members.isLoading}
        caption={t('members.title')}
        labels={{ loading: t('common.loading'), sortBy: (column) => t('common.sortBy', { column }) }}
        empty={<EmptyState icon={Users} title={t('members.title')} />}
      />

      {/*
        Invitations sit under the member list rather than mixed into it: they are
        not members yet, their roles are a promise rather than a fact, and the
        actions that apply to them (resend / revoke) apply to nothing else.
      */}
      <section className="mt-8 space-y-3">
        <h2 className="text-sm font-medium text-muted-foreground">{t('members.pending')}</h2>
        <DataTable
          rows={pending}
          columns={invitationColumns}
          rowKey={(inv) => inv.id}
          isLoading={invitations.isLoading}
          caption={t('members.pending')}
          labels={{ loading: t('common.loading'), sortBy: (column) => t('common.sortBy', { column }) }}
          empty={
            <p className="rounded-lg border border-dashed px-4 py-6 text-center text-sm text-muted-foreground">
              {t('members.pendingNone')}
            </p>
          }
        />
      </section>

      <Dialog open={inviteOpen} onOpenChange={setInviteOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('members.invite')}</DialogTitle>
          </DialogHeader>
          {inviteLink ? (
            <div className="space-y-3">
              <p className="text-sm text-muted-foreground">
                {t('members.inviteEmailed', { email: invitedEmail })}
              </p>
              <div className="flex items-center gap-2">
                <Input readOnly value={inviteLink} className="text-xs" />
                <CopyButton value={inviteLink} />
              </div>
            </div>
          ) : (
            <div className="space-y-4">
              <div className="space-y-1.5">
                <Label>{t('members.email')}</Label>
                <Input
                  type="email"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  placeholder="user@example.com"
                />
              </div>
              <div className="space-y-2">
                <Label>{t('members.roles')}</Label>
                <div className="space-y-1.5">
                  {(roles.data ?? []).map((r) => (
                    <label key={r.id} className="flex items-center gap-2 text-sm">
                      <Checkbox
                        checked={inviteRoles.has(r.id)}
                        onCheckedChange={(v) =>
                          setInviteRoles((prev) => {
                            const next = new Set(prev);
                            if (v === true) next.add(r.id);
                            else next.delete(r.id);
                            return next;
                          })
                        }
                      />
                      {r.name}
                    </label>
                  ))}
                </div>
              </div>
            </div>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={() => setInviteOpen(false)}>
              {t('actions.close')}
            </Button>
            {!inviteLink ? (
              <Button disabled={!email.trim() || invite.isPending} onClick={() => invite.mutate()}>
                {t('members.invite')}
              </Button>
            ) : null}
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Page>
  );
}
