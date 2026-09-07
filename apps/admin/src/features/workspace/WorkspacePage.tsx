import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Building2, Crown, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  ConfirmDeleteDialog,
  EmptyState,
  Input,
  Label,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  toastApiError,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { setCurrentTenantSlug } from '../../tenants';

interface WorkspaceMember {
  membershipId: string;
  userId: string;
  email: string;
}

interface Workspace {
  tenantId: string;
  slug: string;
  name: string;
  status: string;
  createdAt: string;
  owners: { userId: string; email: string }[];
  /** Whether the caller may transfer or delete — owner role, or platform SuperAdmin. */
  isOwner: boolean;
  members: WorkspaceMember[];
  counts: {
    members: number;
    sites: number;
    domains: number;
    mediaAssets: number;
    contentItems: number;
  };
}

export function WorkspacePage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [name, setName] = useState('');
  const [transferTo, setTransferTo] = useState('');
  const [deleteOpen, setDeleteOpen] = useState(false);

  const workspace = useQuery({
    queryKey: ['workspace'],
    queryFn: () => api.get<Workspace>('/admin/tenant'),
  });

  // Seed the rename field once the workspace loads; a later refetch must not
  // overwrite what the operator is currently typing.
  useEffect(() => {
    if (workspace.data && name === '') setName(workspace.data.name);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspace.data?.tenantId]);

  const rename = useMutation({
    mutationFn: () => api.patch('/admin/tenant', { name: name.trim() }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['workspace'] });
      await qc.invalidateQueries({ queryKey: ['me-tenants'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const transfer = useMutation({
    mutationFn: () => api.post<{ ownerEmail: string }>('/admin/tenant/transfer', { membershipId: transferTo }),
    onSuccess: async (result) => {
      toast.success(t('workspace.transferred', { email: result.ownerEmail }));
      setTransferTo('');
      await qc.invalidateQueries({ queryKey: ['workspace'] });
      // The caller has just given away the Owner role, so their own permission set
      // changed — without this the nav keeps showing pages they can no longer open.
      await qc.invalidateQueries({ queryKey: ['me-permissions'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const remove = useMutation({
    mutationFn: () => api.del<{ storageFailed: boolean }>('/admin/tenant'),
    onSuccess: (result) => {
      toast[result?.storageFailed ? 'warning' : 'success'](
        t(result?.storageFailed ? 'workspace.deletedPartial' : 'workspace.deleted'),
      );
      // Everything cached is about a workspace that no longer exists, and the
      // selected-tenant header would 404 every subsequent call. Clear the selection
      // and reload into the tenant picker rather than trying to patch state.
      setCurrentTenantSlug(null);
      window.location.assign('/');
    },
    onError: (e) => toastApiError(e, t),
  });

  if (workspace.isLoading) return <CenteredSpinner />;
  if (workspace.isError || !workspace.data) {
    return (
      <Page>
        <EmptyState icon={Building2} title={t('tenant.selectFirst')} />
      </Page>
    );
  }

  const ws = workspace.data;
  const transferCandidates = ws.members.filter((m) => !ws.owners.some((o) => o.userId === m.userId));

  return (
    <Page>
      <PageHeader title={t('workspace.title')} description={ws.slug} />

      <div className="space-y-6">
        <Card>
          <CardContent className="space-y-4 p-5">
            <div className="space-y-1.5">
              <Label>{t('tenant.displayName')}</Label>
              <div className="flex gap-2">
                <Input value={name} onChange={(e) => setName(e.target.value)} />
                <Button
                  disabled={!name.trim() || name.trim() === ws.name || rename.isPending}
                  onClick={() => rename.mutate()}
                >
                  {t('actions.save')}
                </Button>
              </div>
              {/* The slug is the tenant header, the Forgejo org name and part of
                  every stored object key, so it is fixed after creation. */}
              <p className="text-xs text-muted-foreground">{t('workspace.slugFixed', { slug: ws.slug })}</p>
            </div>

            <div className="flex flex-wrap gap-2 text-sm">
              <Badge tone="secondary">{t('workspace.countMembers', { count: ws.counts.members })}</Badge>
              <Badge tone="secondary">{t('workspace.countSites', { count: ws.counts.sites })}</Badge>
              <Badge tone="secondary">{t('workspace.countDomains', { count: ws.counts.domains })}</Badge>
              <Badge tone="secondary">{t('workspace.countMedia', { count: ws.counts.mediaAssets })}</Badge>
              <Badge tone="secondary">{t('workspace.countContent', { count: ws.counts.contentItems })}</Badge>
            </div>

            <div className="flex flex-wrap items-center gap-2 text-sm">
              <Crown className="h-4 w-4 text-muted-foreground" />
              <span className="text-muted-foreground">{t('workspace.owner')}:</span>
              {ws.owners.length === 0 ? (
                <span className="text-destructive">{t('workspace.noOwner')}</span>
              ) : (
                ws.owners.map((o) => (
                  <Badge key={o.userId} tone="default">
                    {o.email}
                  </Badge>
                ))
              )}
            </div>
          </CardContent>
        </Card>

        {/* Transfer and delete are ownership acts: holding tenant:settings opens this
            page, but only an owner (or the platform SuperAdmin) may run them. */}
        {ws.isOwner ? (
          <>
            <Card>
              <CardContent className="space-y-3 p-5">
                <div>
                  <h2 className="font-medium">{t('workspace.transferTitle')}</h2>
                  <p className="text-sm text-muted-foreground">{t('workspace.transferHint')}</p>
                </div>
                {transferCandidates.length === 0 ? (
                  <p className="text-sm text-muted-foreground">{t('workspace.transferNoCandidates')}</p>
                ) : (
                  <div className="flex flex-wrap gap-2">
                    <Select value={transferTo} onValueChange={setTransferTo}>
                      <SelectTrigger className="w-72">
                        <SelectValue placeholder={t('workspace.transferPick')} />
                      </SelectTrigger>
                      <SelectContent>
                        {transferCandidates.map((m) => (
                          <SelectItem key={m.membershipId} value={m.membershipId}>
                            {m.email}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                    <Button
                      variant="outline"
                      disabled={!transferTo || transfer.isPending}
                      onClick={() => {
                        const target = transferCandidates.find((m) => m.membershipId === transferTo);
                        if (target && window.confirm(t('workspace.transferConfirm', { email: target.email }))) {
                          transfer.mutate();
                        }
                      }}
                    >
                      <Crown className="h-4 w-4" /> {t('workspace.transfer')}
                    </Button>
                  </div>
                )}
              </CardContent>
            </Card>

            <Card className="border-destructive/40">
              <CardContent className="space-y-3 p-5">
                <div>
                  <h2 className="font-medium text-destructive">{t('workspace.dangerZone')}</h2>
                  <p className="text-sm text-muted-foreground">{t('workspace.deleteHint')}</p>
                </div>
                <Button variant="destructive" onClick={() => setDeleteOpen(true)}>
                  <Trash2 className="h-4 w-4" /> {t('workspace.delete')}
                </Button>
              </CardContent>
            </Card>
          </>
        ) : null}
      </div>

      <ConfirmDeleteDialog
        open={deleteOpen}
        onOpenChange={setDeleteOpen}
        title={t('workspace.deleteTitle', { name: ws.name })}
        description={t('workspace.deleteDescription')}
        consequences={[
          t('workspace.deleteConsequenceSites', { count: ws.counts.sites }),
          t('workspace.deleteConsequenceContent', {
            content: ws.counts.contentItems,
            media: ws.counts.mediaAssets,
          }),
          t('workspace.deleteConsequenceMembers', { count: ws.counts.members }),
          t('workspace.deleteConsequenceDomains', { count: ws.counts.domains }),
        ]}
        confirmationValue={ws.slug}
        confirmLabel={t('workspace.delete')}
        pending={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
    </Page>
  );
}
