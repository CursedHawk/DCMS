import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Pencil, Plus, ShieldCheck, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  CenteredSpinner,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  Page,
  PageHeader,
  Table,
  TBody,
  TD,
  TH,
  THead,
  toastApiError,
  TR,
} from '@dcms/admin-ui';
import { api } from '../../lib/api';
import { type Role, usePermissionCatalog, useRoles } from '../rbac/api';
import { PermissionMatrix } from './PermissionMatrix';

export function RolesPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const roles = useRoles();
  const catalog = usePermissionCatalog();
  const [editing, setEditing] = useState<Role | null>(null);
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());

  function openCreate() {
    setEditing(null);
    setName('');
    setSelected(new Set());
    setOpen(true);
  }
  function openEdit(role: Role) {
    setEditing(role);
    setName(role.name);
    setSelected(new Set(role.permissions));
    setOpen(true);
  }

  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), permissions: [...selected] };
      return editing
        ? api.put(`/admin/roles/${editing.id}`, body)
        : api.post('/admin/roles', body);
    },
    onSuccess: async () => {
      toast.success(t('common.saved'));
      setOpen(false);
      await qc.invalidateQueries({ queryKey: ['roles'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.del(`/admin/roles/${id}`),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['roles'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const nameById = new Map((catalog.data ?? []).map((p) => [p.key, p.displayName]));

  return (
    <Page>
      <PageHeader
        title={t('roles.title')}
        actions={
          <Button onClick={openCreate}>
            <Plus className="h-4 w-4" /> {t('roles.createRole')}
          </Button>
        }
      />

      {roles.isLoading ? (
        <CenteredSpinner />
      ) : roles.data && roles.data.length > 0 ? (
        <Table>
          <THead>
            <TR>
              <TH>{t('common.name')}</TH>
              <TH className="w-24">{t('nav.members')}</TH>
              <TH>{t('roles.permissions')}</TH>
              <TH className="text-right">{t('common.actions')}</TH>
            </TR>
          </THead>
          <TBody>
            {roles.data.map((r) => (
              <TR key={r.id}>
                <TD className="font-medium">
                  {r.name}
                  {r.isSystem ? (
                    <Badge tone="secondary" className="ml-2">
                      {t('roles.system')}
                    </Badge>
                  ) : null}
                </TD>
                <TD className="text-sm text-muted-foreground tabular-nums">{r.memberCount}</TD>
                <TD>
                  <div className="flex flex-wrap gap-1">
                    {r.permissions.slice(0, 4).map((p) => (
                      <Badge key={p} tone="outline">
                        {nameById.get(p) ?? p}
                      </Badge>
                    ))}
                    {r.permissions.length > 4 ? (
                      <Badge tone="outline">+{r.permissions.length - 4}</Badge>
                    ) : null}
                    {r.permissions.length === 0 ? (
                      <span className="text-xs text-muted-foreground">—</span>
                    ) : null}
                  </div>
                </TD>
                <TD className="text-right">
                  <div className="flex justify-end gap-1">
                    <Button size="icon" variant="ghost" onClick={() => openEdit(r)}>
                      <Pencil className="h-4 w-4" />
                    </Button>
                    {!r.isSystem ? (
                      <Button
                        size="icon"
                        variant="ghost"
                        className="text-destructive"
                        title={t('actions.delete')}
                        aria-label={t('actions.delete')}
                        onClick={() => {
                          // Deleting a role silently strips whatever it granted from
                          // everyone holding it, so say how many people that is.
                          const message =
                            r.memberCount > 0
                              ? t('roles.deleteConfirmInUse', { name: r.name, count: r.memberCount })
                              : t('roles.deleteConfirm', { name: r.name });
                          if (window.confirm(message)) remove.mutate(r.id);
                        }}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    ) : null}
                  </div>
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
      ) : (
        <EmptyState
          icon={ShieldCheck}
          title={t('roles.title')}
          action={
            <Button onClick={openCreate}>
              <Plus className="h-4 w-4" /> {t('roles.createRole')}
            </Button>
          }
        />
      )}

      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent wide>
          <DialogHeader>
            <DialogTitle>{editing ? t('actions.edit') : t('roles.createRole')}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <div className="space-y-1.5">
              <Label>{t('common.name')}</Label>
              <Input
                value={name}
                onChange={(e) => setName(e.target.value)}
                disabled={editing?.isSystem}
                placeholder="Editor"
              />
            </div>
            <div>
              <Label className="mb-2 block">{t('roles.permissionMatrix')}</Label>
              <PermissionMatrix
                catalog={catalog.data ?? []}
                selected={selected}
                onToggle={(key, on) =>
                  setSelected((prev) => {
                    const next = new Set(prev);
                    if (on) next.add(key);
                    else next.delete(key);
                    return next;
                  })
                }
                onSetMany={(keys, on) =>
                  setSelected((prev) => {
                    const next = new Set(prev);
                    for (const key of keys) {
                      if (on) next.add(key);
                      else next.delete(key);
                    }
                    return next;
                  })
                }
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setOpen(false)}>
              {t('actions.cancel')}
            </Button>
            <Button disabled={!name.trim() || save.isPending} onClick={() => save.mutate()}>
              {t('actions.save')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Page>
  );
}
