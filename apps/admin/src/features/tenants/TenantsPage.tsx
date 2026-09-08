import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Boxes, Plus, ScrollText } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  type Column,
  DataTable,
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
  EmptyState,
  Input,
  Label,
  Page,
  PageHeader,
  toastApiError,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { Perm, can, useMyPermissions } from '../../lib/permissions';
import { setCurrentTenantSlug } from '../../tenants';
import { AuditLogViewer } from '../audit/AuditLogViewer';

interface TenantRow {
  tenantId: string;
  slug: string;
  name: string;
  status?: string;
}

export function TenantsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const [slug, setSlug] = useState('');
  const [name, setName] = useState('');
  const [auditing, setAuditing] = useState<TenantRow | null>(null);

  const me = useMyPermissions(true);
  const canAudit = can(me.data, Perm.AuditRead);

  const tenants = useQuery({
    queryKey: ['tenants'],
    queryFn: () => api.get<TenantRow[]>('/admin/tenants'),
  });

  const create = useMutation({
    mutationFn: () => api.post<{ slug: string }>('/admin/tenants', { slug: slug.trim(), name: name.trim() || slug.trim() }),
    onSuccess: async () => {
      setCurrentTenantSlug(slug.trim());
      toast.success(t('common.saved'));
      setOpen(false);
      setSlug('');
      setName('');
      await qc.invalidateQueries({ queryKey: ['tenants'] });
      await qc.invalidateQueries({ queryKey: ['me-tenants'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const columns: Column<TenantRow>[] = [
    {
      id: 'name',
      header: t('common.name'),
      primary: true,
      cell: (tn) => <span className="font-medium">{tn.name}</span>,
      sortValue: (tn) => tn.name.toLowerCase(),
    },
    {
      id: 'slug',
      header: t('common.slug'),
      cell: (tn) => <span className="font-mono text-xs text-muted-foreground">{tn.slug}</span>,
      sortValue: (tn) => tn.slug,
    },
    {
      id: 'status',
      header: t('common.status'),
      cell: (tn) => <Badge tone="secondary">{tn.status ?? '—'}</Badge>,
      sortValue: (tn) => tn.status ?? '',
    },
    {
      id: 'actions',
      header: '',
      srHeader: t('common.actions'),
      align: 'right',
      cell: (tn) => (
        <div className="flex justify-end gap-2">
          {canAudit ? (
            // Opens that tenant's log in place. Deliberately not "switch tenant, then go to
            // /audit": the switch reloads the whole app and drops whoever was mid-investigation
            // back to the start.
            <Button size="sm" variant="outline" onClick={() => setAuditing(tn)}>
              <ScrollText className="h-4 w-4" aria-hidden /> {t('nav.audit')}
            </Button>
          ) : null}
          <Button
            size="sm"
            variant="outline"
            onClick={() => {
              setCurrentTenantSlug(tn.slug);
              window.location.reload();
            }}
          >
            {t('tenant.use')}
          </Button>
        </div>
      ),
    },
  ];

  return (
    <Page>
      <PageHeader
        title={t('nav.tenants')}
        description={t('tenant.selectFirst')}
        actions={
          <Dialog open={open} onOpenChange={setOpen}>
            <DialogTrigger asChild>
              <Button>
                <Plus className="h-4 w-4" /> {t('tenant.createTenant')}
              </Button>
            </DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>{t('tenant.createTenant')}</DialogTitle>
              </DialogHeader>
              <div className="space-y-3">
                <div className="space-y-1.5">
                  <Label>{t('common.slug')}</Label>
                  <Input value={slug} onChange={(e) => setSlug(e.target.value)} placeholder="acme-co" />
                </div>
                <div className="space-y-1.5">
                  <Label>{t('tenant.displayName')}</Label>
                  <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Acme Co." />
                </div>
              </div>
              <DialogFooter>
                <Button variant="outline" onClick={() => setOpen(false)}>
                  {t('actions.cancel')}
                </Button>
                <Button disabled={!slug.trim() || create.isPending} onClick={() => create.mutate()}>
                  {t('actions.create')}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        }
      />

      <DataTable
        rows={tenants.data}
        columns={columns}
        rowKey={(tn) => tn.tenantId}
        isLoading={tenants.isLoading}
        // A tenant list that failed to load and a platform with no tenants look identical
        // unless one of them says so, and here the failure is nearly always "you are not a
        // SuperAdmin" — which is a sentence, not an empty table.
        error={tenants.isError ? <EmptyState icon={Boxes} title={t('tenant.superAdminOnly')} /> : undefined}
        empty={<EmptyState icon={Boxes} title={t('common.noResults')} />}
        caption={t('nav.tenants')}
        defaultSort={{ columnId: 'name' }}
        labels={{ loading: t('common.loading'), sortBy: (column) => t('common.sortBy', { column }) }}
      />

      <TenantAuditDialog tenant={auditing} onClose={() => setAuditing(null)} />
    </Page>
  );
}

/**
 * One tenant's audit log, without leaving the tenants list.
 *
 * <p>Keyed on the tenant slug so switching from one tenant to another rebuilds the viewer
 * rather than reusing its filter state and cached first page — carrying tenant A's filters
 * silently into tenant B's log would make the two easy to confuse.</p>
 */
function TenantAuditDialog({ tenant, onClose }: { tenant: TenantRow | null; onClose: () => void }) {
  const { t } = useTranslation();

  return (
    <Dialog open={tenant !== null} onOpenChange={(next) => !next && onClose()}>
      <DialogContent wide className="max-h-[85dvh] sm:max-w-5xl">
        <DialogHeader>
          <DialogTitle>{t('audit.title')}</DialogTitle>
          {tenant ? (
            // Name the tenant plainly and keep it on screen: the single mistake worth designing
            // against here is reading one tenant's history believing it to be another's.
            <DialogDescription>
              {tenant.name} · {tenant.slug}
            </DialogDescription>
          ) : null}
        </DialogHeader>
        <DialogBody>
          {tenant ? <AuditLogViewer key={tenant.tenantId} tenant={tenant.slug} /> : null}
        </DialogBody>
      </DialogContent>
    </Dialog>
  );
}
